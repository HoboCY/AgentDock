using AgentDock.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace AgentDock.Desktop;

public static partial class SmokeTest
{
    private static async Task CheckOnboardingAsync(App app, string outputPath, List<string> checks)
    {
        var main = app.Main;
        var tabs = main.GetVisualDescendants().OfType<TabControl>().Single();
        await UntilAsync(() => !main.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("正在处理…", StringComparison.Ordinal) == true));
        if (!OnboardingProgress.IsComplete(app.State.Api.Client.Home) && (tabs.SelectedItem as TabItem)?.Header as string != "开始使用")
            throw new InvalidOperationException("An incomplete first launch must open the setup guide.");
        Capture(main, outputPath + ".onboarding.png");
        checks.Add("first-launch-opens-onboarding");
        var originalWidth = main.Width;
        var originalHeight = main.Height;
        main.Width = 900;
        main.Height = 600;
        foreach (var label in new[] { "1  启动后台", "2  保存凭据", "3  接入 Codex", "4  启用委派" })
        {
            ClickSetup(main, label);
            await Task.Delay(150);
            foreach (var action in new[] { "稍后配置", "重新检查", "上一步" })
            {
                var button = SetupButton(main, action);
                var point = button.TranslatePoint(default, main)!.Value;
                if (point.X < 200 || point.Y < 0 || point.X + button.Bounds.Width > main.Bounds.Width || point.Y + button.Bounds.Height > main.Bounds.Height)
                    throw new InvalidOperationException("Setup navigation must remain visible at 900 x 600.");
            }
            Capture(main, outputPath + ".onboarding-step" + label[0] + ".png");
        }
        checks.Add("four-setup-steps-and-compact-navigation");
        ClickSetup(main, "稍后配置");
        if (tabs.SelectedIndex != 0 || OnboardingProgress.IsComplete(app.State.Api.Client.Home))
            throw new InvalidOperationException("Deferring must not mark setup complete.");
        checks.Add("deferral-keeps-onboarding-incomplete");
        main.Width = originalWidth;
        main.Height = originalHeight;

        if (Environment.GetEnvironmentVariable("AGENTDOCK_SMOKE_ONBOARDING") != "1") return;
        var evidenceRoot = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        if (!ProjectPaths.Within(ProjectPaths.Canonicalize(app.State.Api.Client.Home), ProjectPaths.Canonicalize(evidenceRoot)) ||
            !ProjectPaths.Within(ProjectPaths.Canonicalize(Environment.GetEnvironmentVariable("CODEX_HOME")!), ProjectPaths.Canonicalize(evidenceRoot)))
            throw new InvalidOperationException("Onboarding mutation smoke requires isolated data and Codex homes under the evidence directory.");
        if (!app.State.HostReachable || app.State.Configuration?.CredentialConfigured != false)
            throw new InvalidOperationException("Onboarding credential smoke requires a fresh, connected test host.");
        tabs.SelectedIndex = 1;
        await Task.Delay(200);
        ClickSetup(main, "打开配置引导");
        await UntilAsync(() => SetupButton(main, "重新检查").IsEnabled);
        ClickSetup(main, "2  保存凭据");
        ClickSetup(main, "保存并继续");
        await UntilAsync(() => main.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "请输入并保存 OpenCode Go API Key。"));
        var key = main.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "引导 OpenCode Go 凭据");
        key.Text = "synthetic-onboarding-key-not-real";
        ClickSetup(main, "保存并继续");
        await UntilAsync(() => app.State.Configuration?.CredentialConfigured == true && SetupButton(main, "重新检查").IsEnabled);
        if (!string.IsNullOrEmpty(key.Text)) throw new InvalidOperationException("A saved credential must leave no text in the input.");
        checks.Add("wizard-validates-empty-key-and-clears-saved-key");

        ClickSetup(main, "3  接入 Codex");
        ClickSetup(main, "接入 Codex 并继续");
        await UntilAsync(() => main.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Content, "启用全局委派并完成") && b.IsEnabled));
        ClickSetup(main, "启用全局委派并完成");
        await UntilAsync(() => tabs.SelectedIndex == 0 && OnboardingProgress.IsComplete(app.State.Api.Client.Home));
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")!;
        var routing = new GlobalRouting(codexHome, app.State.Api.Client.Home);
        if (!routing.Inspect().Enabled) throw new InvalidOperationException("Completing setup must persist the global rule.");
        checks.Add("codex-cli-registration-and-guide-completion");
        var rule = File.ReadAllText(routing.Inspect().Path);
        if (!rule.Contains("status.executionProfile.model", StringComparison.Ordinal) ||
            !rule.Contains("status.executionProfile.reasoningEffort", StringComparison.Ordinal) ||
            app.State.Host?.ExecutionProfile is not { Provider.Length: > 0, Model.Length: > 0, ReasoningEffort.Length: > 0 })
            throw new InvalidOperationException("The guide must persist dispatch reporting instructions backed by live host metadata.");
        checks.Add("dispatch-rule-and-live-host-execution-profile");
        tabs.SelectedIndex = 1;
        await Task.Delay(200);
        ClickSetup(main, "打开配置引导");
        await UntilAsync(() => SetupButton(main, "重新检查").IsEnabled);
        ClickSetup(main, "4  启用委派");
        ClickSetup(main, "关闭全局委派并恢复原规则");
        await UntilAsync(() => !routing.Inspect().Enabled);
        checks.Add("global-rule-can-be-disabled-from-guide");

        tabs.SelectedIndex = 1;
        await Task.Delay(200);
        var concurrency = main.GetVisualDescendants().OfType<NumericUpDown>().First();
        concurrency.Value = 7;
        var keyEditor = main.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "OpenCode Go 凭据");
        // Simulate a previously unconfigured view to exercise the actual settings save handler.
        var known = app.State.Configuration!;
        app.State.ApplyConfiguration(known with { CredentialConfigured = false });
        concurrency.Value = 7;
        keyEditor.Text = "synthetic-settings-key-not-real";
        ClickSetup(main, "保存凭据");
        await UntilAsync(() => main.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "凭据：已配置"));
        if (!string.IsNullOrEmpty(keyEditor.Text) || concurrency.Value != 7)
            throw new InvalidOperationException("Saving credentials must clear the key and preserve unsaved settings.");
        checks.Add("credential-save-refreshes-label-and-preserves-unsaved-settings");
    }

    private static Button SetupButton(MainWindow window, string label) => window.GetVisualDescendants().OfType<Button>()
        .Single(b => Equals(b.Content, label) && b.IsEffectivelyVisible);

    private static void ClickSetup(MainWindow window, string label)
    {
        var button = SetupButton(window, label);
        if (!button.IsEnabled) throw new InvalidOperationException("Expected enabled setup action: " + label);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Setup UI did not reach the expected state.");
            await Task.Delay(100);
        }
    }
}
