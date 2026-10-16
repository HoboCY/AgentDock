using System.Runtime.InteropServices;
using System.Text;
using AgentDock.Core;

namespace AgentDock.Engine;

/// <summary>
/// Current-user generic-password storage backed by the macOS Security
/// framework. Each AgentDock data home gets its own item: the service name is
/// fixed and the canonical data home is the account, so one data home never
/// reads or overwrites another home's credential. The item is explicitly not
/// synchronizable, and the key only ever crosses the boundary as in-process
/// CFData - never through a shell <c>security</c> invocation, a command line or
/// a child process environment.
///
/// Core Foundation ownership: every object created here follows the create rule
/// and is released exactly once by <see cref="Scope"/>; every value returned by
/// the Security APIs is copied out and released.
/// </summary>
internal static class MacKeychain
{
    /// <summary>Keychain service shared by every AgentDock data home.</summary>
    internal const string ServiceName = "AgentDock.OpenCodeGo";

    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // kCFStringEncodingUTF8
    private const uint Utf8Encoding = 0x08000100;

    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private const int DuplicateItem = -25299;
    private const int InteractionNotAllowed = -25308;
    private const int InteractionRequired = -25315;
    private const int AuthFailed = -25293;
    private const int UserCanceled = -128;
    private const int MissingEntitlement = -34018;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The service/account pair for a data home. The account is the full path
    /// with any trailing directory separator removed so equivalent spellings of
    /// the same home address the same Keychain item.
    /// </summary>
    internal static (string Service, string Account) Identity(string home)
    {
        var canonical = ProjectPaths.Canonicalize(home);
        return (ServiceName, canonical);
    }

    /// <summary>
    /// True when a matching item exists. Reads only the item's attributes, not
    /// the stored secret data.
    /// </summary>
    internal static bool Exists(string home)
    {
        var identity = Identity(home);
        using var scope = new Scope();
        var query = scope.Dictionary(
            (Native.SecClass, Native.SecClassGenericPassword),
            (Native.SecAttrService, scope.String(identity.Service)),
            (Native.SecAttrAccount, scope.String(identity.Account)),
            (Native.SecAttrSynchronizable, Native.BooleanFalse),
            (Native.SecReturnAttributes, Native.BooleanTrue),
            (Native.SecMatchLimit, Native.SecMatchLimitOne));
        var status = SecItemCopyMatching(query, out var result);
        if (result != IntPtr.Zero)
        {
            CFRelease(result);
        }
        return status switch
        {
            Success => true,
            ItemNotFound => false,
            _ => throw Failure(status, "find"),
        };
    }

    /// <summary>Stores <paramref name="key"/> for the current user, replacing any existing item.</summary>
    internal static void Save(string home, string key)
    {
        var identity = Identity(home);
        using var scope = new Scope();
        var service = scope.String(identity.Service);
        var account = scope.String(identity.Account);
        var secret = scope.Data(Encoding.UTF8.GetBytes(key));
        var query = scope.Dictionary(
            (Native.SecClass, Native.SecClassGenericPassword),
            (Native.SecAttrService, service),
            (Native.SecAttrAccount, account),
            (Native.SecAttrSynchronizable, Native.BooleanFalse));
        var update = scope.Dictionary((Native.SecValueData, secret));
        var status = SecItemUpdate(query, update);
        if (status == Success)
        {
            return;
        }
        if (status != ItemNotFound)
        {
            throw Failure(status, "update");
        }
        var attributes = scope.Dictionary(
            (Native.SecClass, Native.SecClassGenericPassword),
            (Native.SecAttrService, service),
            (Native.SecAttrAccount, account),
            (Native.SecAttrSynchronizable, Native.BooleanFalse),
            (Native.SecValueData, secret));
        status = SecItemAdd(attributes, out var added);
        if (added != IntPtr.Zero)
        {
            CFRelease(added);
        }
        if (status == DuplicateItem)
        {
            // Another writer created the item between update and add.
            status = SecItemUpdate(query, update);
        }
        if (status != Success)
        {
            throw Failure(status, "store");
        }
    }

    /// <summary>Returns the stored key, or null when no item exists for this home.</summary>
    internal static string? Read(string home)
    {
        var identity = Identity(home);
        using var scope = new Scope();
        var query = scope.Dictionary(
            (Native.SecClass, Native.SecClassGenericPassword),
            (Native.SecAttrService, scope.String(identity.Service)),
            (Native.SecAttrAccount, scope.String(identity.Account)),
            (Native.SecAttrSynchronizable, Native.BooleanFalse),
            (Native.SecReturnData, Native.BooleanTrue),
            (Native.SecMatchLimit, Native.SecMatchLimitOne));
        var status = SecItemCopyMatching(query, out var result);
        if (status == ItemNotFound)
        {
            if (result != IntPtr.Zero)
            {
                CFRelease(result);
            }
            return null;
        }
        if (status != Success)
        {
            if (result != IntPtr.Zero)
            {
                CFRelease(result);
            }
            throw Failure(status, "read");
        }
        if (result == IntPtr.Zero)
        {
            throw new DockException("credential-invalid", "The stored OpenCode Go key could not be decoded.");
        }
        try
        {
            if (CFGetTypeID(result) != CFDataGetTypeID())
            {
                throw new DockException("credential-invalid", "The stored OpenCode Go key has an unexpected format.");
            }
            var length = CFDataGetLength(result);
            var pointer = CFDataGetBytePtr(result);
            if (length <= 0 || length > int.MaxValue || pointer == IntPtr.Zero)
            {
                throw new DockException("credential-invalid", "The stored OpenCode Go key could not be decoded.");
            }
            var bytes = new byte[(int)length];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new DockException("credential-invalid", "The stored OpenCode Go key could not be decoded.");
            }
        }
        finally
        {
            CFRelease(result);
        }
    }

    /// <summary>Deletes the item for a data home. Used by tests to clean up only their own items.</summary>
    internal static void Delete((string Service, string Account) identity)
    {
        using var scope = new Scope();
        var query = scope.Dictionary(
            (Native.SecClass, Native.SecClassGenericPassword),
            (Native.SecAttrService, scope.String(identity.Service)),
            (Native.SecAttrAccount, scope.String(identity.Account)),
            (Native.SecAttrSynchronizable, Native.BooleanFalse));
        var status = SecItemDelete(query);
        if (status is Success or ItemNotFound)
        {
            return;
        }
        throw Failure(status, "delete");
    }

    private static DockException Failure(int status, string operation) => status switch
    {
        InteractionNotAllowed or InteractionRequired => new DockException("credential-locked",
            "The macOS Keychain is locked or requires user interaction to " + operation + " the OpenCode Go credential."),
        AuthFailed or UserCanceled or MissingEntitlement => new DockException("credential-access-denied",
            "The macOS Keychain denied access while trying to " + operation + " the OpenCode Go credential."),
        _ => new DockException("credential-unavailable",
            "The macOS Keychain could not " + operation + " the OpenCode Go credential (OSStatus " + status + ")."),
    };

    /// <summary>Owns every Core Foundation object created during one operation and releases them once.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly List<IntPtr> _objects = [];

        public IntPtr Own(IntPtr value)
        {
            if (value == IntPtr.Zero)
            {
                throw new DockException("credential-unavailable", "A macOS Keychain request could not be allocated.");
            }
            _objects.Add(value);
            return value;
        }

        public IntPtr String(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            return Own(CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, Utf8Encoding, 0));
        }

        public IntPtr Data(byte[] value) => Own(CFDataCreate(IntPtr.Zero, value, value.Length));

        public IntPtr Dictionary(params (IntPtr Key, IntPtr Value)[] pairs)
        {
            var keys = new IntPtr[pairs.Length];
            var values = new IntPtr[pairs.Length];
            for (var i = 0; i < pairs.Length; i++)
            {
                keys[i] = pairs[i].Key;
                values[i] = pairs[i].Value;
            }
            return Own(CFDictionaryCreate(IntPtr.Zero, keys, values, pairs.Length, Native.KeyCallbacks, Native.ValueCallbacks));
        }

        public void Dispose()
        {
            // The type callbacks make the dictionary retain its keys and values,
            // so releasing in reverse creation order is safe.
            for (var i = _objects.Count - 1; i >= 0; i--)
            {
                CFRelease(_objects[i]);
            }
        }
    }

    /// <summary>Exported constants resolved lazily; nothing here runs on non-macOS platforms.</summary>
    private static class Native
    {
        private static readonly IntPtr Security = NativeLibrary.Load(SecurityFramework);
        private static readonly IntPtr CoreFoundation = NativeLibrary.Load(CoreFoundationFramework);

        internal static readonly IntPtr KeyCallbacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        internal static readonly IntPtr ValueCallbacks = NativeLibrary.GetExport(CoreFoundation, "kCFTypeDictionaryValueCallBacks");
        internal static readonly IntPtr BooleanFalse = Constant(CoreFoundation, "kCFBooleanFalse");
        internal static readonly IntPtr BooleanTrue = Constant(CoreFoundation, "kCFBooleanTrue");

        internal static readonly IntPtr SecClass = Constant(Security, "kSecClass");
        internal static readonly IntPtr SecClassGenericPassword = Constant(Security, "kSecClassGenericPassword");
        internal static readonly IntPtr SecAttrService = Constant(Security, "kSecAttrService");
        internal static readonly IntPtr SecAttrAccount = Constant(Security, "kSecAttrAccount");
        internal static readonly IntPtr SecAttrSynchronizable = Constant(Security, "kSecAttrSynchronizable");
        internal static readonly IntPtr SecValueData = Constant(Security, "kSecValueData");
        internal static readonly IntPtr SecReturnData = Constant(Security, "kSecReturnData");
        internal static readonly IntPtr SecReturnAttributes = Constant(Security, "kSecReturnAttributes");
        internal static readonly IntPtr SecMatchLimit = Constant(Security, "kSecMatchLimit");
        internal static readonly IntPtr SecMatchLimitOne = Constant(Security, "kSecMatchLimitOne");

        // The kSec* and kCF* constants are exported pointer variables; their value is the CF object.
        private static IntPtr Constant(IntPtr library, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    [DllImport(SecurityFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SecItemAdd(IntPtr attributes, out IntPtr result);

    [DllImport(SecurityFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

    [DllImport(SecurityFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

    [DllImport(SecurityFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SecItemDelete(IntPtr query);

    [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint numBytes, uint encoding,
        byte isExternalRepresentation);

    [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint numBytes);

    [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint CFGetTypeID(IntPtr value);

    [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern nint CFDataGetTypeID();

    [DllImport(CoreFoundationFramework, CallingConvention = CallingConvention.Cdecl)]
    private static extern void CFRelease(IntPtr value);
}
