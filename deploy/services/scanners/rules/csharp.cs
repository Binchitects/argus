// The weak code each rule in csharp.yaml must find (ruleid) and the fixed code it must not (ok).
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Xml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

public class Samples : Controller
{
    public void Sql(SqlConnection conn, AppDb db, string name, int id)
    {
        // ruleid: csharp-sql-built-from-strings
        var a = new SqlCommand($"SELECT * FROM Users WHERE Name = '{name}'", conn);
        // ruleid: csharp-sql-built-from-strings
        var b = new SqlCommand("SELECT * FROM Users WHERE Id = " + id, conn);
        var c = conn.CreateCommand();
        // ruleid: csharp-sql-built-from-strings
        c.CommandText = "DELETE FROM Users WHERE Name = '" + name + "'";
        // ok: csharp-sql-built-from-strings
        var d = new SqlCommand("SELECT * FROM Users WHERE Name = @name", conn);
        d.Parameters.AddWithValue("@name", name);
        // ok: csharp-sql-built-from-strings
        c.CommandText = "SELECT 1";
        // ruleid: csharp-raw-sql-built-from-strings
        db.Users.FromSqlRaw($"SELECT * FROM Users WHERE Name = '{name}'");
        // ruleid: csharp-raw-sql-built-from-strings
        db.Database.ExecuteSqlRaw("DELETE FROM Users WHERE Id = " + id);
        // ok: csharp-raw-sql-built-from-strings
        db.Users.FromSqlInterpolated($"SELECT * FROM Users WHERE Name = {name}");
        // ok: csharp-raw-sql-built-from-strings
        db.Users.FromSqlRaw("SELECT * FROM Users WHERE Name = {0}", name);
    }

    public IActionResult User(SqlConnection conn)
    {
        var name = Request.Query["name"];
        // ruleid: csharp-sql-from-request, csharp-sql-built-from-strings
        var cmd = new SqlCommand("SELECT * FROM Users WHERE Name = '" + name + "'", conn);
        // ok: csharp-sql-from-request
        var fine = new SqlCommand("SELECT * FROM Users WHERE Name = @name", conn);
        return Ok();
    }

    public void Commands(string file, string host)
    {
        // ruleid: csharp-command-from-data
        Process.Start(file);
        // ruleid: csharp-command-from-data
        Process.Start("cmd.exe", $"/c ping {host}");
        var psi = new ProcessStartInfo("ping");
        // ruleid: csharp-command-from-data
        psi.Arguments = "-n 1 " + host;
        // ok: csharp-command-from-data
        Process.Start("notepad.exe");
        // ok: csharp-command-from-data
        psi.ArgumentList.Add(host);
        // ok: csharp-command-from-data
        Process.Start(psi);
    }

    public void Deserialise(Stream s)
    {
        // ruleid: csharp-unsafe-deserialization
        var f = new BinaryFormatter();
        // ruleid: csharp-unsafe-deserialization
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
        // ok: csharp-unsafe-deserialization
        var fine = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None };
    }

    public void Tls()
    {
        var handler = new HttpClientHandler();
        // ruleid: csharp-tls-not-verified
        handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;
        // ruleid: csharp-tls-not-verified
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        // ok: csharp-tls-not-verified
        handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => errors == System.Net.Security.SslPolicyErrors.None;
    }

    public void Xml(string text)
    {
        var doc = new XmlDocument();
        // ruleid: csharp-xml-external-entities
        doc.XmlResolver = new XmlUrlResolver();
        var settings = new XmlReaderSettings();
        // ruleid: csharp-xml-external-entities
        settings.DtdProcessing = DtdProcessing.Parse;
        // ok: csharp-xml-external-entities
        settings.DtdProcessing = DtdProcessing.Prohibit;
    }

    public object Html(string comment)
    {
        // ruleid: csharp-html-not-escaped
        var a = new HtmlString("<p>" + comment + "</p>");
        // ok: csharp-html-not-escaped
        var b = new HtmlString("<br>");
        return (a, b);
    }

    public void Crypto(byte[] data)
    {
        // ruleid: csharp-weak-hash
        using var md5 = MD5.Create();
        // ruleid: csharp-weak-hash
        var sha1 = SHA1.HashData(data);
        // ok: csharp-weak-crypto, csharp-weak-hash
        var sha256 = SHA256.HashData(data);
        // ruleid: csharp-weak-crypto
        using var des = DES.Create();
        // ok: csharp-weak-crypto
        using var aes = Aes.Create();
    }

    public void Secrets()
    {
        var random = new Random();
        // ruleid: csharp-predictable-secret
        var resetCode = random.Next(100000, 999999);
        // ok: csharp-predictable-secret
        var delay = random.Next(10, 100);
        // ok: csharp-predictable-secret
        var apiKey = RandomNumberGenerator.GetHexString(32);
    }

    public void Jwt(TokenValidationParameters p)
    {
        // ruleid: csharp-jwt-signature-not-required
        p.RequireSignedTokens = false;
        // ok: csharp-jwt-signature-not-required
        p.RequireSignedTokens = true;
    }

    public IActionResult Download()
    {
        var name = Request.Query["file"];
        // ruleid: csharp-path-from-request
        return PhysicalFile(Path.Combine("/srv/files", name), "application/octet-stream");
    }

    public IActionResult Download2()
    {
        var name = Path.GetFileName(Request.Query["file"]);
        // ok: csharp-path-from-request
        return PhysicalFile(Path.Combine("/srv/files", name), "application/octet-stream");
    }

    public IActionResult Go()
    {
        var next = Request.Query["next"];
        // ruleid: csharp-redirect-from-request
        return Redirect(next);
    }

    public IActionResult Go2()
    {
        // ok: csharp-redirect-from-request
        return Redirect("/home");
    }

    public async Task<string> Fetch(HttpClient http)
    {
        var url = Request.Query["url"];
        // ruleid: csharp-request-to-url-from-request
        return await http.GetStringAsync(url);
    }

    public async Task<string> Fetch2(HttpClient http)
    {
        // ok: csharp-request-to-url-from-request
        return await http.GetStringAsync("https://example.com/status");
    }

    // System.Web's request, and outside data that is not a request.
    public void Web(System.Web.HttpRequest req, System.Web.HttpResponse resp, SqlConnection conn)
    {
        string data = req.QueryString["id"];
        // ruleid: csharp-html-from-request
        resp.Write("<br>Hello " + data);
        // ok: csharp-html-from-request
        resp.Write("<br>Hello " + System.Web.HttpUtility.HtmlEncode(data));
        // ruleid: csharp-redirect-from-request
        resp.Redirect(data);
        var de = new System.DirectoryServices.DirectoryEntry("LDAP://example.org");
        var search = new System.DirectoryServices.DirectorySearcher(de);
        // ruleid: csharp-ldap-from-request
        search.Filter = "(&(objectClass=user)(employeename=" + data + "))";
        // ok: csharp-ldap-from-request
        search.Filter = "(&(objectClass=user)(employeename=guest))";
        var doc = new XmlDocument();
        System.Xml.XPath.XPathNavigator nav = doc.CreateNavigator();
        // ruleid: csharp-xpath-from-request
        var secret = (string)nav.Evaluate("//users/user[name/text()='" + data + "']/secret/text()");
        // ok: csharp-xpath-from-request
        var plain = (string)nav.Evaluate("//users/user[name/text()='guest']/secret/text()");
    }

    public void Socket(SqlConnection conn)
    {
        string data;
        using (var tcpConn = new System.Net.Sockets.TcpClient("host.example.org", 39544))
        {
            System.Net.Sockets.TcpClient client = tcpConn;
            using (StreamReader sr = new StreamReader(client.GetStream()))
            {
                data = sr.ReadLine();
            }
        }
        using (SqlCommand command = new SqlCommand(null, conn))
        {
            // ruleid: csharp-sql-from-request, csharp-sql-built-from-strings
            command.CommandText = "insert into users (status) values ('updated') where name='" + data + "'";
        }
        string root = "C:\\uploads\\";
        // ruleid: csharp-path-from-request
        using (StreamReader sr = new StreamReader(root + data)) { }
        var code = new System.Text.StringBuilder("public class Calculator { public int Sum() { return 10 + ");
        code.Append(data);
        code.Append("; } }");
        var provider = System.CodeDom.Compiler.CodeDomProvider.CreateProvider("CSharp");
        // ruleid: csharp-code-from-request
        provider.CompileAssemblyFromSource(new System.CodeDom.Compiler.CompilerParameters(), code.ToString());
    }

    public void Console2(SqlConnection conn)
    {
        string data = Console.ReadLine();
        // ruleid: csharp-command-from-data
        Process.Start(data);
    }

    public void Cookies(System.Web.HttpResponse resp)
    {
        var open = new System.Web.HttpCookie("SecretMessage", "test");
        // ruleid: csharp-cookie-not-secure
        resp.Cookies.Add(open);
        var closed = new System.Web.HttpCookie("SecretMessage", "test");
        closed.Secure = true;
        // ok: csharp-cookie-not-secure
        resp.Cookies.Add(closed);
        // ruleid: csharp-cookie-not-secure
        closed.Secure = false;
    }
}
