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
        // ruleid: java-weak-crypto
        MessageDigest.getInstance("MD5");
        // ruleid: java-weak-crypto
        Cipher.getInstance("AES/ECB/PKCS5Padding");
        // ruleid: java-weak-crypto
        Cipher.getInstance("AES");
        // ok: java-weak-crypto
        Cipher.getInstance("AES/GCM/NoPadding");
        // ok: java-weak-crypto
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

    void security(HttpSecurity http) throws Exception {
        // ruleid: java-spring-csrf-disabled
        http.csrf().disable();
        // ruleid: java-spring-csrf-disabled
        http.csrf(c -> c.disable());
        // ok: java-spring-csrf-disabled
        http.csrf(c -> c.ignoringRequestMatchers("/webhook"));
    }
}
