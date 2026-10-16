using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentDock.Desktop;

/// <summary>任务结果窗口：摘要、正文可用状态、结果与事件，只读。</summary>
public sealed class ResultWindow : Window
{
    private readonly AppState _state;
    private readonly TextBlock _head = new() { FontSize = 15, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _meta = new() { TextWrapping = TextWrapping.Wrap, Opacity = .85, FontSize = 12.5 };
    private readonly TextBlock _bodyState = new() { FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#B26A00")), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _result = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
    private readonly ListBox _events = new();
    private readonly Button _refresh;

    public string TaskId { get; }

    public ResultWindow(string taskId, AppState state)
    {
        TaskId = taskId;
        _state = state;
        Title = "任务结果";
        Icon = Icons.Create();
        Width = 820;
        Height = 580;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _refresh = new Button { Content = "刷新", MinHeight = 28, Padding = new Thickness(12, 4) };
        _refresh.Click += async (_, _) => await LoadAsync();

        var top = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        Grid.SetColumn(_head, 0);
        Grid.SetColumn(_refresh, 1);
        top.Children.Add(_head);
        top.Children.Add(_refresh);

        var resultLabel = new TextBlock { Text = "结果", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        var eventsLabel = new TextBlock { Text = "事件", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 4) };
        var root = new Grid { Margin = new Thickness(16), RowSpacing = 4 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(top, 0);
        Grid.SetRow(_meta, 1);
        Grid.SetRow(_bodyState, 2);
        Grid.SetRow(resultLabel, 3);
        Grid.SetRow(_result, 4);
        Grid.SetRow(eventsLabel, 5);
        Grid.SetRow(_events, 6);
        root.Children.Add(top);
        root.Children.Add(_meta);
        root.Children.Add(_bodyState);
        root.Children.Add(resultLabel);
        root.Children.Add(_result);
        root.Children.Add(eventsLabel);
        root.Children.Add(_events);
        Content = root;
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _refresh.IsEnabled = false;
        try
        {
            var detail = await _state.Api.GetAsync(TaskId, 0, _state.Token);
            var events = detail.Events.ToList();
            while (detail.HasMore && events.Count < 2000)
            {
                detail = await _state.Api.GetAsync(TaskId, detail.NextCursor, _state.Token);
                events.AddRange(detail.Events);
            }
            var summary = detail.Summary;
            Title = "任务结果 — " + summary.Title;
            _head.Text = summary.Title;
            _meta.Text =
                $"{Fmt.Status(summary.Status)} · {summary.Project} · 角色 {summary.Role}\n" +
                $"输入 Token {Fmt.Tokens(summary.InputTokens)} · 输出 Token {Fmt.Tokens(summary.OutputTokens)} · 生成速度 {Fmt.Speed(summary.GenerationTokensPerSecond)} · 用时 {Fmt.Elapsed(summary, DateTimeOffset.Now)}" +
                (summary.Error is null ? "" : "\n错误：" + summary.Error);
            _bodyState.Text = detail.BodyState == "expired" ? "任务正文已清理，摘要仍可查看。" : "";
            _bodyState.IsVisible = detail.BodyState == "expired";
            _result.Text = detail.Result ?? (detail.BodyState == "expired" ? "" : "结果尚未产生。");
            _events.ItemsSource = events
                .Select(e => $"{e.At.ToLocalTime():HH:mm:ss} [{e.Kind}] {e.Text}")
                .ToArray();
        }
        catch (Exception ex)
        {
            _meta.Text = "加载失败：" + AppState.Describe(ex);
            _result.Text = "";
            _events.ItemsSource = Array.Empty<string>();
        }
        finally { _refresh.IsEnabled = true; }
    }
}
