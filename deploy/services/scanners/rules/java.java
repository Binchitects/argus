// The weak code each rule in java.yaml must find (ruleid) and the fixed code it must not (ok).
package samples;

import java.beans.XMLDecoder;
import java.io.*;
import java.net.URL;
import java.nio.file.*;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;
import java.sql.*;
import java.util.Random;
import javax.crypto.Cipher;
import javax.net.ssl.*;
import javax.servlet.http.*;
import javax.xml.XMLConstants;
import javax.xml.parsers.DocumentBuilderFactory;
import com.fasterxml.jackson.databind.ObjectMapper;
import org.springframework.web.bind.annotation.*;
import org.yaml.snakeyaml.Yaml;
import org.yaml.snakeyaml.constructor.SafeConstructor;

public class Samples {
    void sql(Connection conn, Statement st, String name, int id) throws Exception {
        // ruleid: java-sql-built-from-strings
        st.executeQuery("SELECT * FROM users WHERE name = '" + name + "'");
        // ruleid: java-sql-built-from-strings
        st.executeUpdate(String.format("DELETE FROM users WHERE id = %d", id));
        String q = "SELECT * FROM users WHERE id = " + id;
        // ruleid: java-sql-built-from-strings
        conn.prepareStatement(q);
        // ok: java-sql-built-from-strings
        PreparedStatement ps = conn.prepareStatement("SELECT * FROM users WHERE name = ?");
        ps.setString(1, name);
        // ok: java-sql-built-from-strings
        st.executeQuery("SELECT 1");
    }

    void servlet(HttpServletRequest req, HttpServletResponse resp, Statement st) throws Exception {
        String name = req.getParameter("name");
        // ruleid: java-sql-from-request, java-sql-built-from-strings
        st.executeQuery("SELECT * FROM users WHERE name = '" + name + "'");
        // ok: java-sql-from-request
        st.executeQuery("SELECT * FROM users");
        // ruleid: java-command-from-request, java-command-from-data
        Runtime.getRuntime().exec(req.getParameter("cmd"));
        // ruleid: java-path-from-request
        new FileInputStream("/srv/files/" + req.getParameter("file"));
        // ruleid: java-redirect-from-request
        resp.sendRedirect(req.getParameter("next"));
        // ok: java-redirect-from-request
        resp.sendRedirect("/home");
        // ruleid: java-request-to-url-from-request
        new URL(req.getParameter("url")).openStream();
        // ok: java-request-to-url-from-request
        new URL("https://example.com/status").openStream();
        // ruleid: java-html-from-request
        resp.getWriter().println("<p>" + req.getParameter("q") + "</p>");
        // ok: java-html-from-request
        resp.getWriter().println("<p>done</p>");
    }

    @GetMapping("/user")
    String spring(@RequestParam String name, Statement st) throws Exception {
        // ruleid: java-sql-from-request, java-sql-built-from-strings
        st.executeQuery("SELECT * FROM users WHERE name = '" + name + "'");
        return "ok";
    }

    void commands(String host) throws Exception {
        // ruleid: java-command-from-data
        Runtime.getRuntime().exec("ping -c 1 " + host);
        // ruleid: java-command-from-data
        new ProcessBuilder("sh", "-c", "ping -c 1 " + host);
        // ok: java-command-from-data
        new ProcessBuilder("ping", "-c", "1", host);
        // ok: java-command-from-data
        Runtime.getRuntime().exec("uptime");
    }

    void deserialise(InputStream in, String text) throws Exception {
        ObjectInputStream ois = new ObjectInputStream(in);
        // ruleid: java-unsafe-deserialization
        ois.readObject();
        // ruleid: java-unsafe-deserialization
        new XMLDecoder(in);
        ObjectMapper mapper = new ObjectMapper();
        // ruleid: java-unsafe-deserialization
        mapper.enableDefaultTyping();
        // ruleid: java-unsafe-deserialization
        new Yaml().load(text);
        // ok: java-unsafe-deserialization
        new Yaml(new SafeConstructor(new LoaderOptions())).load(text);
    }

    void deserialise2(InputStream in) throws Exception {
        ObjectInputStream ois = new ObjectInputStream(in);
        ois.setObjectInputFilter(ObjectInputFilter.Config.createFilter("samples.*;!*"));
        // ok: java-unsafe-deserialization
        ois.readObject();
    }

    void xml(InputStream in) throws Exception {
        DocumentBuilderFactory f = DocumentBuilderFactory.newInstance();
        // ruleid: java-xml-external-entities
        f.newDocumentBuilder().parse(in);
    }

    void xml2(InputStream in) throws Exception {
        DocumentBuilderFactory f = DocumentBuilderFactory.newInstance();
        f.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true);
        // ok: java-xml-external-entities
        f.newDocumentBuilder().parse(in);
    }

    static class TrustAll implements X509TrustManager {
        public void checkClientTrusted(X509Certificate[] chain, String authType) {}
        // ruleid: java-tls-not-verified
        public void checkServerTrusted(X509Certificate[] chain, String authType) {}
        public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
    }

    void hosts(HttpsURLConnection c) {
        // ruleid: java-tls-not-verified
        c.setHostnameVerifier((hostname, session) -> true);
        // ok: java-tls-not-verified
        c.setHostnameVerifier(HttpsURLConnection.getDefaultHostnameVerifier());
    }

    void crypto() throws Exception {
        // ruleid: java-weak-hash
        MessageDigest.getInstance("MD5");
        // ruleid: java-weak-hash
        java.security.MessageDigest.getInstance("SHA1", "SUN");
        // ok: java-weak-hash
        java.security.MessageDigest.getInstance("SHA-512");
        // ruleid: java-weak-crypto
        Cipher.getInstance("AES/ECB/PKCS5Padding");
        // ruleid: java-weak-crypto
        javax.crypto.Cipher.getInstance("DES/CBC/PKCS5Padding", java.security.Security.getProvider("SunJCE"));
        // ok: java-weak-crypto
        javax.crypto.Cipher.getInstance("AES/CBC/PKCS5Padding");
        // ruleid: java-weak-crypto
        Cipher.getInstance("AES");
        // ok: java-weak-crypto
        Cipher.getInstance("AES/GCM/NoPadding");
        // ok: java-weak-crypto, java-weak-hash
        MessageDigest.getInstance("SHA-256");
    }

    void secrets() {
        Random random = new Random();
        // ruleid: java-predictable-secret
        String resetToken = Long.toHexString(random.nextLong());
        // ok: java-predictable-secret
        int jitter = random.nextInt(100);
        // ok: java-predictable-secret
        String apiKey = Long.toHexString(new SecureRandom().nextLong());
    }

    void remembered(HttpServletRequest req, HttpServletResponse resp) {
        double value = new java.util.Random().nextDouble();
        String remember = Double.toString(value).substring(2);
        // ruleid: java-predictable-value-kept
        javax.servlet.http.Cookie cookie = new javax.servlet.http.Cookie("rememberMe", remember);
        // ruleid: java-predictable-value-kept
        req.getSession().setAttribute("rememberMe", remember);
        byte[] bytes = new byte[16];
        new SecureRandom().nextBytes(bytes);
        // ok: java-predictable-value-kept
        req.getSession().setAttribute("rememberMe", java.util.Base64.getEncoder().encodeToString(bytes));
    }

    void outside(HttpServletRequest request, HttpServletResponse response, java.sql.Connection conn) throws Exception {
        String header = request.getHeaders("referer").nextElement();
        String bar = new Helper().doSomething(header);
        // ruleid: java-path-from-request
        java.io.FileInputStream in = new java.io.FileInputStream(new java.io.File("/srv/files/", bar));
        // ruleid: java-path-from-request
        java.nio.file.Files.newInputStream(java.nio.file.Paths.get("/srv/files/" + bar));
        Runtime r = Runtime.getRuntime();
        // ruleid: java-command-from-request, java-command-from-data
        r.exec("echo " + bar);
        // ruleid: java-sql-from-request
        conn.prepareCall("{call lookup('" + bar + "')}");
        // ruleid: java-html-from-request
        response.getWriter().format(java.util.Locale.US, bar, "x");
        // ok: java-html-from-request
        response.getWriter().print(org.owasp.esapi.ESAPI.encoder().encodeForHTML(bar));
        javax.naming.directory.DirContext ctx = new javax.naming.directory.InitialDirContext();
        // ruleid: java-ldap-from-request
        ctx.search("ou=users", "(uid=" + bar + ")", new javax.naming.directory.SearchControls());
        // ok: java-ldap-from-request
        ctx.search("ou=users", "(uid={0})", new Object[] {bar}, new javax.naming.directory.SearchControls());
        javax.xml.xpath.XPath xp = javax.xml.xpath.XPathFactory.newInstance().newXPath();
        // ruleid: java-xpath-from-request
        xp.evaluate("/users/user[@name='" + bar + "']", new org.xml.sax.InputSource(in));
        // ruleid: java-session-from-request
        request.getSession().setAttribute("userid", bar);
        // ok: java-session-from-request
        request.getSession().setAttribute("userid", "guest");
    }

    void socket(java.sql.Statement st) throws Exception {
        java.net.Socket socket = new java.net.Socket("host.example.org", 39544);
        BufferedReader reader = new BufferedReader(new InputStreamReader(socket.getInputStream(), "UTF-8"));
        String data = reader.readLine();
        // ruleid: java-sql-from-request, java-sql-built-from-strings
        st.executeQuery("SELECT * FROM users WHERE name = '" + data + "'");
    }

    void cookies(HttpServletResponse response) {
        javax.servlet.http.Cookie open = new javax.servlet.http.Cookie("session", "x");
        // ruleid: java-cookie-not-secure
        open.setSecure(false);
        // ruleid: java-cookie-not-secure
        response.addCookie(open);
        javax.servlet.http.Cookie closed = new javax.servlet.http.Cookie("session", "x");
        closed.setSecure(true);
        closed.setHttpOnly(true);
        // ok: java-cookie-not-secure
        response.addCookie(closed);
    }

    static class Helper {
        String doSomething(String param) {
            return param.split(" ")[0];
        }
    }

    void security(HttpSecurity http) throws Exception {
        // ruleid: java-spring-csrf-disabled
        http.csrf().disable();
        // ruleid: java-spring-csrf-disabled
        http.csrf(c -> c.disable());
        // ok: java-spring-csrf-disabled
        http.csrf(c -> c.ignoringRequestMatchers("/webhook"));
    }
}
