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
    /// </summary>
    sealed class FakeGitLab : IDisposable
    {
        readonly HttpListener _listener = new();
        public string Url { get; }
        public List<string> Posts { get; } = [];

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
                var form = ctx.Request.HasEntityBody ? new StreamReader(ctx.Request.InputStream).ReadToEnd() : "";
                if (ctx.Request.HttpMethod == "POST")
                {
                    lock (Posts) Posts.Add(path);
                }
                var (status, body) = (ctx.Request.HttpMethod, path) switch
                {
                    ("GET", "/users/sign_in") => (200,
                        "<form action=\"/users/sign_in\"><input name=\"authenticity_token\" value=\"t1\"></form>" +
                        "<form action=\"/users/auth/ldapmain/callback\"><input name=\"authenticity_token\" value=\"t1\"></form>"),
                    // Only the local account has a password GitLab keeps.
                    ("POST", "/users/sign_in") when form.Contains("user%5Blogin%5D=local", StringComparison.Ordinal) && form.Contains("secret", StringComparison.Ordinal)
                        => Session(ctx),
                    ("POST", "/users/sign_in") => (200, "Invalid login or password."),
                    // The LDAP server knows the LDAP account.
                    ("POST", "/users/auth/ldapmain/callback") when form.Contains("username=ldapuser", StringComparison.Ordinal) && form.Contains("secret", StringComparison.Ordinal)
                        => Session(ctx),
                    ("POST", "/users/auth/ldapmain/callback") => (200, "Could not authenticate you from Ldapmain because \"Invalid credentials\"."),
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

        static (int, string) Session(HttpListenerContext ctx)
        {
            ctx.Response.AppendCookie(new Cookie("_gitlab_session", "ok", "/"));
            return (200, "Welcome");
        }

        public void Dispose() => _listener.Close();
    }

    static GitLabConfig Config(FakeGitLab gitlab, string user, string ldap = "") =>
        GitLabConfig.Create(gitlab.Url, auth: "password", username: user, password: "secret", ldap: ldap);

    [Fact]
    public void An_LDAP_account_signs_in_on_GitLabs_LDAP_form_after_its_own_refuses_it()
    {
        using var gitlab = new FakeGitLab();
        Assert.Equal(("Authorization", "Bearer glpat-made"), Credentials.Credential(Config(gitlab, "ldapuser")));
        Assert.Equal(["/users/sign_in", "/users/auth/ldapmain/callback", "/-/user_settings/personal_access_tokens"], gitlab.Posts);
    }

    [Fact]
    public void A_local_account_never_tries_LDAP_and_a_named_LDAP_server_goes_straight_there()
    {
        using var local = new FakeGitLab();
        Assert.Equal(("Authorization", "Bearer glpat-made"), Credentials.Credential(Config(local, "local")));
        Assert.DoesNotContain("/users/auth/ldapmain/callback", local.Posts);

        using var named = new FakeGitLab();
        Assert.Equal(("Authorization", "Bearer glpat-made"), Credentials.Credential(Config(named, "ldapuser", ldap: "ldapmain")));
        Assert.DoesNotContain("/users/sign_in", named.Posts);
    }

    [Fact]
    public void LDAP_off_an_LDAP_server_the_page_does_not_offer_and_a_wrong_password_each_say_why()
    {
        using var gitlab = new FakeGitLab();
        var off = Assert.Throws<CredentialError>(() => Credentials.Credential(Config(gitlab, "ldapuser", ldap: "off")));
        Assert.Contains("GitLab's own sign-in", off.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/users/auth/ldapmain/callback", gitlab.Posts);

        var missing = Assert.Throws<CredentialError>(() => Credentials.Credential(Config(gitlab, "ldapuser2", ldap: "ldapsecondary")));
        Assert.Contains("offers only ldapmain", missing.Message, StringComparison.Ordinal);

        var wrong = Assert.Throws<CredentialError>(() => Credentials.Credential(Config(gitlab, "nobody")));
        Assert.Contains("GitLab's own sign-in and the LDAP sign-in ldapmain", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("the username is the LDAP one", wrong.Message, StringComparison.Ordinal);
    }
}
