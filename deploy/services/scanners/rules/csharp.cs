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
        // ruleid: csharp-weak-crypto
        using var md5 = MD5.Create();
        // ruleid: csharp-weak-crypto
        var sha1 = SHA1.HashData(data);
        // ok: csharp-weak-crypto
        var sha256 = SHA256.HashData(data);
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
}
