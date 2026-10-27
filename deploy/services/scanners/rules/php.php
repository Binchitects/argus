<?php
// The weak code each rule in php.yaml must find (ruleid) and the fixed code it must not (ok).

function sql($conn, $pdo, $name, $id) {
    // ruleid: php-sql-built-from-strings
    mysqli_query($conn, "SELECT * FROM users WHERE id = " . $id);
    // ruleid: php-sql-built-from-strings
    $pdo->query("SELECT * FROM users WHERE name = '$name'");
    // ok: php-sql-built-from-strings
    $st = $pdo->prepare("SELECT * FROM users WHERE name = ?");
    $st->execute([$name]);
    // ok: php-sql-built-from-strings
    $pdo->query("SELECT 1");
}

function user($pdo) {
    $name = $_GET['name'];
    // ruleid: php-sql-from-request, php-sql-built-from-strings
    $pdo->query("SELECT * FROM users WHERE name = '" . $name . "'");
    // ok: php-sql-from-request
    $pdo->query("SELECT * FROM users");
}

function ping() {
    $host = $_GET['host'];
    // ruleid: php-command-from-request, php-command-from-strings
    system("ping -c 1 " . $host);
    // ok: php-command-from-request, php-command-from-strings
    system("ping -c 1 " . escapeshellarg($host));
}

function code($expr) {
    // ruleid: php-code-from-data
    eval($expr);
    // ok: php-code-from-data
    eval("return 1;");
}

function page() {
    // ruleid: php-include-from-request
    include $_GET['page'];
}

function page2() {
    // ok: php-include-from-request
    include 'pages/home.php';
}

function deserialise($cookie) {
    // ruleid: php-unsafe-deserialization
    $a = unserialize($cookie);
    // ok: php-unsafe-deserialization
    $b = unserialize($cookie, ['allowed_classes' => false]);
    // ok: php-unsafe-deserialization
    $c = json_decode($cookie, true);
}

function greet() {
    // ruleid: php-html-from-request
    echo "Hello " . $_GET['name'];
    // ok: php-html-from-request
    echo "Hello " . htmlspecialchars($_GET['name'], ENT_QUOTES);
}

function download() {
    // ruleid: php-path-from-request
    readfile("/srv/files/" . $_GET['file']);
    // ok: php-path-from-request
    readfile("/srv/files/" . basename($_GET['file']));
}

function tls($ch) {
    // ruleid: php-tls-not-verified
    curl_setopt($ch, CURLOPT_SSL_VERIFYPEER, false);
    // ok: php-tls-not-verified
    curl_setopt($ch, CURLOPT_SSL_VERIFYPEER, true);
}

function hashes($password, $data) {
    // ruleid: php-weak-password-hash
    $h = md5($password);
    // ok: php-weak-password-hash
    $e = md5($data);
    // ok: php-weak-password-hash
    $p = password_hash($password, PASSWORD_DEFAULT);
}
