using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeArena;

/// <summary>A guest's user and password. Never shown: its text is the user alone, and <see cref="Hide"/> blanks the password in output.</summary>
internal sealed class GuestLogin(string user, string password)
{
    public string User { get; } = user;
    public string Password { get; } = password;

    /// <summary>The text with the password blanked (one of three characters or more: a shorter one is in too many words).</summary>
    public string Hide(string text) => Password.Length >= 3 ? text.Replace(Password, "••••", StringComparison.Ordinal) : text;

    public override string ToString() => User;
}

/// <summary>What the person is asked for a VM's sign-in: the user it had (when any), and whether the guest refused the last one.</summary>
internal sealed record LoginQuestion(string Id, string Vm, string Hypervisor, string? User, bool Refused, bool CanSave, string Keyring);

/// <summary>What the person gave: the sign-in, and whether to keep it in the keyring.</summary>
internal sealed class LoginAnswer(GuestLogin login, bool save)
{
    public GuestLogin Login { get; } = login;
    public bool Save { get; } = save;
}

/// <summary>
/// The guests' sign-ins: the person's keyring first (Secret Service on Linux, the Keychain on macOS, Credential Manager
/// on Windows), else asked (in the IDE's dialog, or the terminal with the password unseen), kept for the session in
/// memory only. They are never in the model's conversation, the session's file or the log: tools blank them in what
/// they return.
/// </summary>
internal sealed class GuestLogins(Keyring keyring, Ui ui)
{
    private readonly Dictionary<string, GuestLogin> _session = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _asking = new(1, 1);

    public Keyring Keyring { get; } = keyring;

    /// <summary>Asks the IDE's page instead of the terminal, when set; null back: the person cancelled.</summary>
    public Func<LoginQuestion, CancellationToken, Task<LoginAnswer?>>? Asker { get; set; }

    /// <summary>The name a VM's sign-in is kept under: its hypervisor and its name.</summary>
    public static string Id(VmInfo vm) => $"{vm.Host.Kind}:{vm.Name}";

    /// <summary>The sign-in for a VM; a <see cref="ToolError"/> when there is none and nobody can be asked, or the person cancels.</summary>
    public async Task<GuestLogin> GetAsync(VmInfo vm, CancellationToken ct)
    {
        var id = Id(vm);
        await _asking.WaitAsync(ct);
        try
        {
            if (_session.TryGetValue(id, out var known))
            {
                return known;
            }
            var refused = _refused.Contains(id);
            var kept = await Keyring.ReadAsync(id, ct);
            if (!refused && kept is not null)
            {
                return _session[id] = kept;
            }
            var question = new LoginQuestion(id, vm.Name, vm.Host.Title, kept?.User, refused, Keyring.CanSave, Keyring.Name);
            var answer = Asker is { } asker ? await asker(question, ct) : AskInTerminal(question);
            if (answer is null)
            {
                throw new ToolError(Asker is null && !ui.CanAsk
                    ? $"{vm.Name} needs the guest's user and password, and this run cannot ask for them. The person keeps them in the keyring with: code-arena vm login \"{vm.Name}\""
                    : $"The person did not give {vm.Name}'s sign-in. Ask them what to do instead.");
            }
            _refused.Remove(id);
            if (answer.Save && !await Keyring.SaveAsync(id, answer.Login, ct))
            {
                ui.Warn($"The sign-in for {vm.Name} could not be kept in {Keyring.Name}: it is kept for this session only.");
            }
            return _session[id] = answer.Login;
        }
        finally
        {
            _asking.Release();
        }
    }

    /// <summary>The guest refused this sign-in: it is asked for again next time, the keyring's too.</summary>
    public void Refused(VmInfo vm)
    {
        var id = Id(vm);
        _session.Remove(id);
        _refused.Add(id);
    }

    private LoginAnswer? AskInTerminal(LoginQuestion q)
    {
        if (!ui.CanAsk)
        {
            return null;
        }
        if (q.Refused)
        {
            ui.Warn($"{q.Vm} refused the sign-in{(q.User is null ? "" : $" of {q.User}")}.");
        }
        var user = ui.Answer($"User in {q.Vm} ({q.Hypervisor}){(q.User is null ? "" : $" [{q.User}]")}:")?.Trim();
        if (string.IsNullOrEmpty(user))
        {
            user = q.User;
        }
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }
        var password = ui.Answer($"Password of {user} in {q.Vm}:", secret: true);
        if (password is null)
        {
            return null;
        }
        var save = q.CanSave && ui.Ask($"Keep it in {q.Keyring}?", null) == Approval.Yes;
        return new LoginAnswer(new GuestLogin(user, password), save);
    }
}

/// <summary>Where sign-ins are kept: the system's own keyring, by a name (code-arena-vm, and the VM's id).</summary>
internal abstract class Keyring
{
    public const string Service = "code-arena-vm";

    public abstract string Name { get; }
    public virtual bool CanSave => true;

    public abstract Task<GuestLogin?> ReadAsync(string id, CancellationToken ct);
    public abstract Task<bool> SaveAsync(string id, GuestLogin login, CancellationToken ct);
    public abstract Task<bool> ForgetAsync(string id, CancellationToken ct);

    /// <summary>This system's keyring: secret-tool (libsecret) on Linux, security on macOS, Credential Manager on Windows; none without them.</summary>
    public static Keyring For(Func<string, string?> env)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentials();
        }
        var tool = OperatingSystem.IsMacOS() ? "/usr/bin/security" : (env("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "secret-tool")).FirstOrDefault(File.Exists);
        return tool is null || !File.Exists(tool) ? new NoKeyring() : OperatingSystem.IsMacOS() ? new MacKeychain(tool) : new SecretService(tool);
    }

    /// <summary>Runs the keyring's program; its exit code and output (null when it could not start or took too long).</summary>
    protected static async Task<(int Code, string Output)?> RunAsync(string program, IEnumerable<string> args, string? input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        try
        {
            var r = await Proc.PipeAsync(psi, input ?? "", TimeSpan.FromSeconds(20), 64 * 1024, ct);
            return r.TimedOut ? null : (r.ExitCode, r.Output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

internal sealed class NoKeyring : Keyring
{
    public override string Name => "no keyring (secret-tool is not installed)";
    public override bool CanSave => false;
    public override Task<GuestLogin?> ReadAsync(string id, CancellationToken ct) => Task.FromResult<GuestLogin?>(null);
    public override Task<bool> SaveAsync(string id, GuestLogin login, CancellationToken ct) => Task.FromResult(false);
    public override Task<bool> ForgetAsync(string id, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>The Secret Service (GNOME Keyring, KWallet) through secret-tool: the password the secret, the user an attribute beside the VM's.</summary>
internal sealed class SecretService(string tool) : Keyring
{
    public override string Name => "the keyring (Secret Service)";

    public override async Task<GuestLogin?> ReadAsync(string id, CancellationToken ct)
    {
        if (await RunAsync(tool, ["lookup", "service", Service, "vm", id], null, ct) is not (0, var password) || password.Length == 0)
        {
            return null;
        }
        var user = await RunAsync(tool, ["search", "service", Service, "vm", id], null, ct) is (0, var found)
            ? found.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("attribute.user = ", StringComparison.Ordinal))?["attribute.user = ".Length..]
            : null;
        return user is { Length: > 0 } ? new GuestLogin(user, password.TrimEnd('\n')) : null;
    }

    public override async Task<bool> SaveAsync(string id, GuestLogin login, CancellationToken ct)
    {
        await ForgetAsync(id, ct);
        return await RunAsync(tool, ["store", "--label", $"Code Arena: {id}", "service", Service, "vm", id, "user", login.User], login.Password, ct) is (0, _);
    }

    public override async Task<bool> ForgetAsync(string id, CancellationToken ct) => await RunAsync(tool, ["clear", "service", Service, "vm", id], null, ct) is (0, _);
}

/// <summary>The login Keychain through security: a generic password of service code-arena-vm:ID, its account the user.</summary>
internal sealed class MacKeychain(string tool) : Keyring
{
    public override string Name => "the Keychain";

    public override async Task<GuestLogin?> ReadAsync(string id, CancellationToken ct)
    {
        if (await RunAsync(tool, ["find-generic-password", "-s", $"{Service}:{id}"], null, ct) is not (0, var attributes))
        {
            return null;
        }
        const string mark = "\"acct\"<blob>=\"";
        var at = attributes.IndexOf(mark, StringComparison.Ordinal);
        var user = at < 0 ? null : attributes[(at + mark.Length)..].Split('"')[0];
        return user is { Length: > 0 } && await RunAsync(tool, ["find-generic-password", "-s", $"{Service}:{id}", "-w"], null, ct) is (0, var password)
            ? new GuestLogin(user, password.TrimEnd('\n'))
            : null;
    }

    /// <summary>Through security's own command line on its stdin (-i), so the password is never in a process's arguments.</summary>
    public override async Task<bool> SaveAsync(string id, GuestLogin login, CancellationToken ct)
    {
        static string Q(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        var line = $"add-generic-password -U -s {Q($"{Service}:{id}")} -a {Q(login.User)} -l {Q($"Code Arena: {id}")} -w {Q(login.Password)}\n";
        return await RunAsync(tool, ["-i"], line, ct) is (0, _) && await ReadAsync(id, ct) is not null;
    }

    public override async Task<bool> ForgetAsync(string id, CancellationToken ct) => await RunAsync(tool, ["delete-generic-password", "-s", $"{Service}:{id}"], null, ct) is (0, _);
}

/// <summary>Windows' Credential Manager: a generic credential named code-arena-vm:ID, kept for this user on this machine.</summary>
internal sealed class WindowsCredentials : Keyring
{
    public override string Name => "Credential Manager";

    private const int Generic = 1;
    private const int LocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint WrittenLow;
        public uint WrittenHigh;
        public int BlobSize;
        public IntPtr Blob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    private static string Target(string id) => $"{Service}:{id}";

    public override Task<GuestLogin?> ReadAsync(string id, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || !CredRead(Target(id), Generic, 0, out var found))
        {
            return Task.FromResult<GuestLogin?>(null);
        }
        try
        {
            var c = Marshal.PtrToStructure<Credential>(found);
            var user = Marshal.PtrToStringUni(c.UserName) ?? "";
            var password = c.BlobSize > 0 ? Marshal.PtrToStringUni(c.Blob, c.BlobSize / 2) : "";
            return Task.FromResult<GuestLogin?>(user.Length > 0 ? new GuestLogin(user, password) : null);
        }
        finally
        {
            CredFree(found);
        }
    }

    public override Task<bool> SaveAsync(string id, GuestLogin login, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }
        var target = Marshal.StringToCoTaskMemUni(Target(id));
        var user = Marshal.StringToCoTaskMemUni(login.User);
        var blob = Marshal.StringToCoTaskMemUni(login.Password);
        try
        {
            var c = new Credential
            {
                Type = Generic,
                TargetName = target,
                UserName = user,
                Blob = blob,
                BlobSize = Encoding.Unicode.GetByteCount(login.Password),
                Persist = LocalMachine,
            };
            return Task.FromResult(CredWrite(ref c, 0));
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
            Marshal.FreeCoTaskMem(user);
            Marshal.FreeCoTaskMem(target);
        }
    }

    public override Task<bool> ForgetAsync(string id, CancellationToken ct) => Task.FromResult(OperatingSystem.IsWindows() && CredDelete(Target(id), Generic, 0));
}
