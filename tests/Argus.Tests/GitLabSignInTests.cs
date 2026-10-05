using System.Net;
using System.Net.Sockets;
using System.Text;
using Argus.Configuration;
using Argus.Indexing;

namespace Argus.Tests;

public class GitLabSignInTests
{
    /// <summary>
    /// GitLab's sign-in as Argus meets it: its own form takes a local account; an LDAP account signs in
    /// only on the LDAP form (/users/auth/ldapmain/callback); a signed-in session may make a token.
    /// Each load of the sign-in page gives each form a token of its own (the LDAP form first, as GitLab
    /// shows its LDAP tab first); a post must carry the latest token of its form, and a refused sign-in
    /// makes them all stale.
    /// </summary>
    sealed class FakeGitLab : IDisposable
    {
        readonly HttpListener _listener = new();
        readonly Dictionary<string, string> _tokens = [];
        int _loads;
        public string Url { get; }
        public List<string> Posts { get; } = [];

        /// <summary>Posts refused for their token (a real GitLab answers 422).</summary>
        public int Forged { get; private set; }

        public FakeGitLab()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
                var path = ctx.Request.Url!.AbsolutePath;
                var signedIn = ctx.Request.Cookies["_gitlab_session"]?.Value == "ok";
                var form = Form(ctx.Request.HasEntityBody ? new StreamReader(ctx.Request.InputStream).ReadToEnd() : "");
                if (ctx.Request.HttpMethod == "POST")
                {
                    lock (Posts) Posts.Add(path);
                }
                var (status, body) = (ctx.Request.HttpMethod, path) switch
                {
                    ("GET", "/users/sign_in") => (200, SignInPage()),
                    ("POST", "/users/sign_in" or "/users/auth/ldapmain/callback") when !Fresh(path, form) => (422, "Can't verify CSRF token authenticity."),
                    // Only the local account has a password GitLab keeps.
                    ("POST", "/users/sign_in") when form.GetValueOrDefault("user[login]") == "local" && form.GetValueOrDefault("user[password]") == "secret"
                        => Session(ctx),
                    ("POST", "/users/sign_in") => Refused("Invalid login or password."),
                    // The LDAP server knows the LDAP account.
                    ("POST", "/users/auth/ldapmain/callback") when form.GetValueOrDefault("username") == "ldapuser" && form.GetValueOrDefault("password") == "secret"
                        => Session(ctx),
                    ("POST", "/users/auth/ldapmain/callback") => Refused("Could not authenticate you from Ldapmain because \"Invalid credentials\"."),
                    ("GET", "/api/v4/user") => signedIn ? (200, "{\"id\":7}") : (401, "{\"message\":\"401 Unauthorized\"}"),
                    ("GET", "/-/user_settings/profile") => (200, "<meta name=\"csrf-token\" content=\"c1\">"),
                    ("GET", "/api/v4/personal_access_tokens") => (200, "[]"),
                    ("POST", "/-/user_settings/personal_access_tokens") => signedIn ? (200, "{\"token\":\"glpat-made\"}") : (401, "{}"),
                    _ => (404, "{}"),
                };
                ctx.Response.StatusCode = status;
                var bytes = Encoding.UTF8.GetBytes(body);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        string SignInPage()
        {
            lock (_tokens)
            {
                var load = ++_loads;
                string[] actions = ["/users/auth/ldapmain/callback", "/users/sign_in"];
                _tokens[actions[0]] = $"ldapmain-{load}";
                _tokens[actions[1]] = $"own-{load}";
                return string.Concat(actions.Select(a =>
                    $"<form class=\"gl-show-field-errors\" action=\"{a}\" accept-charset=\"UTF-8\" method=\"post\">" +
                    $"<input type=\"hidden\" name=\"authenticity_token\" value=\"{_tokens[a]}\" autocomplete=\"off\" /></form>"));
            }
        }

        /// <summary>The post carries the latest token of the form it was posted from.</summary>
        bool Fresh(string action, Dictionary<string, string> form)
        {
            lock (_tokens)
            {
                if (_tokens.TryGetValue(action, out var token) && form.GetValueOrDefault("authenticity_token") == token) return true;
                Forged++;
                return false;
            }
        }

        /// <summary>A refused sign-in: the page's tokens are stale from now on.</summary>
        (int, string) Refused(string message)
        {
            lock (_tokens) _tokens.Clear();
            return (200, message);
        }

        /// <summary>A posted form's fields (a field given twice, such as scopes[], keeps its last value).</summary>
        static Dictionary<string, string> Form(string body)
        {
            var fields = new Dictionary<string, string>();
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)))
                fields[Uri.UnescapeDataString(pair[0].Replace('+', ' '))] = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            return fields;
        }

        static (int, string) Session(HttpListenerContext ctx)
        {
            ctx.Response.AppendCookie(new Cookie("_gitlab_session", "ok", "/"));
            return (200, "Welcome");
        }

        public void Dispose() => _listener.Close();
    }

    static GitLabConfig Config(FakeGitLab gitlab, string user, string ldap = "") =>
        GitLabConfig.Create(gitlab.Url, auth: "password", username: user, password: "secret", ldap: ldap);

    /// <summary>A sign-in of its own: the process caches minted tokens by address and user, and a port may come back.</summary>
    static (string, string) SignIn(GitLabConfig cfg)
    {
        Credentials.Invalidate(cfg);
        try { return Credentials.Credential(cfg); }
        finally { Credentials.Invalidate(cfg); }
    }

    [Fact]
    public void An_LDAP_account_signs_in_on_GitLabs_LDAP_form_after_its_own_refuses_it()
    {
        using var gitlab = new FakeGitLab();
        Assert.Equal(("Authorization", "Bearer glpat-made"), SignIn(Config(gitlab, "ldapuser")));
        Assert.Equal(["/users/sign_in", "/users/auth/ldapmain/callback", "/-/user_settings/personal_access_tokens"], gitlab.Posts);
        // Each form's own token, and after the refusal made them stale, the LDAP form's fresh one.
        Assert.Equal(0, gitlab.Forged);
    }

    [Fact]
    public void A_local_account_never_tries_LDAP_and_a_named_LDAP_server_goes_straight_there()
    {
        using var local = new FakeGitLab();
        Assert.Equal(("Authorization", "Bearer glpat-made"), SignIn(Config(local, "local")));
        Assert.DoesNotContain("/users/auth/ldapmain/callback", local.Posts);
        Assert.Equal(0, local.Forged);

        using var named = new FakeGitLab();
        Assert.Equal(("Authorization", "Bearer glpat-made"), SignIn(Config(named, "ldapuser", ldap: "ldapmain")));
        Assert.DoesNotContain("/users/sign_in", named.Posts);
        Assert.Equal(0, named.Forged);
    }

    [Fact]
    public void LDAP_off_an_LDAP_server_the_page_does_not_offer_and_a_wrong_password_each_say_why()
    {
        using var gitlab = new FakeGitLab();
        var off = Assert.Throws<CredentialError>(() => SignIn(Config(gitlab, "ldapuser", ldap: "off")));
        Assert.Contains("GitLab's own sign-in", off.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/users/auth/ldapmain/callback", gitlab.Posts);

        var missing = Assert.Throws<CredentialError>(() => SignIn(Config(gitlab, "ldapuser2", ldap: "ldapsecondary")));
        Assert.Contains("offers only ldapmain", missing.Message, StringComparison.Ordinal);

        var wrong = Assert.Throws<CredentialError>(() => SignIn(Config(gitlab, "nobody")));
        Assert.Contains("GitLab's own sign-in and the LDAP sign-in ldapmain", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("the username is the LDAP one", wrong.Message, StringComparison.Ordinal);
        Assert.Equal(0, gitlab.Forged);
    }
}
