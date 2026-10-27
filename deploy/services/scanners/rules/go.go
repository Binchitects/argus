// The weak code each rule in go.yaml must find (ruleid) and the fixed code it must not (ok).
package samples

import (
	"archive/zip"
	"crypto/md5"
	"crypto/sha256"
	"crypto/tls"
	"database/sql"
	"fmt"
	"html/template"
	"math/rand"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

func sqlQueries(db *sql.DB, name string, id int) {
	// ruleid: go-sql-built-from-strings
	db.Query(fmt.Sprintf("SELECT * FROM users WHERE name = '%s'", name))
	// ruleid: go-sql-built-from-strings
	db.Exec("DELETE FROM users WHERE name = '" + name + "'")
	q := fmt.Sprintf("SELECT * FROM users WHERE id = %d", id)
	// ruleid: go-sql-built-from-strings
	db.QueryRow(q)
	// ok: go-sql-built-from-strings
	db.Query("SELECT * FROM users WHERE name = $1", name)
	// ok: go-sql-built-from-strings
	db.Exec("SELECT 1")
}

func commands(host, program string) {
	// ruleid: go-command-from-data
	exec.Command("sh", "-c", "ping -c 1 "+host)
	// ruleid: go-command-from-data
	exec.Command(program)
	// ok: go-command-from-data
	exec.Command("ping", "-c", "1", host)
	// ok: go-command-from-data
	exec.Command("sh", "-c", "uptime")
}

func tlsConfig() *tls.Config {
	// ruleid: go-tls-not-verified
	return &tls.Config{InsecureSkipVerify: true}
}

func tlsConfig2() *tls.Config {
	// ok: go-tls-not-verified
	return &tls.Config{MinVersion: tls.VersionTLS12}
}

func hashes(data []byte) {
	// ruleid: go-weak-crypto
	md5.Sum(data)
	// ok: go-weak-crypto
	sha256.Sum256(data)
}

func secrets() (string, int) {
	// ruleid: go-predictable-secret
	resetToken := fmt.Sprint(rand.Int63())
	// ok: go-predictable-secret
	jitter := rand.Intn(100)
	return resetToken, jitter
}

func html(comment string) (template.HTML, template.HTML) {
	// ruleid: go-html-not-escaped
	a := template.HTML("<p>" + comment + "</p>")
	// ok: go-html-not-escaped
	b := template.HTML("<br>")
	return a, b
}

func unzip(r *zip.Reader, dir string) {
	for _, f := range r.File {
		// ruleid: go-zip-slip
		path := filepath.Join(dir, f.Name)
		os.WriteFile(path, nil, 0o644)
	}
}

func unzip2(r *zip.Reader, dir string) {
	for _, f := range r.File {
		// ok: go-zip-slip
		p := filepath.Join(dir, f.Name)
		if !strings.HasPrefix(p, filepath.Clean(dir)+string(os.PathSeparator)) {
			continue
		}
		os.WriteFile(p, nil, 0o644)
	}
}

func handler(db *sql.DB) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		name := r.URL.Query().Get("name")
		// ruleid: go-sql-from-request, go-sql-built-from-strings
		db.Query("SELECT * FROM users WHERE name = '" + name + "'")
		// ok: go-sql-from-request
		db.Query("SELECT * FROM users WHERE name = $1", name)
		// ruleid: go-command-from-request, go-command-from-data
		exec.Command(r.FormValue("cmd"))
		// ruleid: go-path-from-request
		os.ReadFile("/srv/files/" + r.FormValue("file"))
		// ok: go-path-from-request
		os.ReadFile(filepath.Join("/srv/files", filepath.Base(r.FormValue("file"))))
		// ruleid: go-request-to-url-from-request
		http.Get(r.URL.Query().Get("url"))
		// ok: go-request-to-url-from-request
		http.Get("https://example.com/status")
		// ruleid: go-redirect-from-request
		http.Redirect(w, r, r.FormValue("next"), http.StatusFound)
		// ok: go-redirect-from-request
		http.Redirect(w, r, "/home", http.StatusFound)
	}
}
