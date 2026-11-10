# GHSA-j8r2-6x86-q33q: Unintended leak of Proxy-Authorization header in requests

CVE-2023-32681 · severity medium (CVSS:3.1/AV:N/AC:H/PR:N/UI:R/S:C/C:H/I:N/A:N) · CWE-200 · published 2023-05-22.

## Affected

| Ecosystem | Package | Versions | Fixed in |
| --- | --- | --- | --- |
| PyPI | `requests` | >= 2.3.0, < 2.31.0 | 2.31.0 |

## Details

### Impact

Since Requests v2.3.0, Requests has been vulnerable to potentially leaking `Proxy-Authorization` headers to destination servers, specifically during redirects to an HTTPS origin. This is a product of how `rebuild_proxies` is used to recompute and [reattach the `Proxy-Authorization` header](https://github.com/psf/requests/blob/f2629e9e3c7ce3c3c8c025bcd8db551101cbc773/requests/sessions.py#L319-L328) to requests when redirected. Note this behavior has _only_ been observed to affect proxied requests when credentials are supplied in the URL user information component (e.g. `https://username:password@proxy:8080`).

**Current vulnerable behavior(s):**

1. HTTP → HTTPS: **leak**
2. HTTPS → HTTP: **no leak**
3. HTTPS → HTTPS: **leak**
4. HTTP → HTTP: **no leak**

For HTTP connections sent through the proxy, the proxy will identify the header in the request itself and remove it prior to forwarding to the destination server. However when sent over HTTPS, the `Proxy-Authorization` header must be sent in the CONNECT request as the proxy has no visibility into further tunneled requests. This results in Requests forwarding the header to the destination server unintentionally, allowing a malicious actor to potentially exfiltrate those credentials.

The reason this currently works for HTTPS connections in Requests is the `Proxy-Authorization` header is also handled by urllib3 with our usage of the ProxyManager in adapters.py with [`proxy_manager_for`](https://github.com/psf/requests/blob/f2629e9e3c7ce3c3c8c025bcd8db551101cbc773/requests/adapters.py#L199-L235). This will compute the required proxy headers in `proxy_headers` and pass them to the Proxy Manager, avoiding attaching them directly to the Request object. This will be our preferred option going forward for default usage.

### Patches
Starting in Requests v2.31.0, Requests will no longer attach this header to redirects with an HTTPS destination. This should have no negative impacts on the default behavior of the library as the proxy credentials are already properly being handled by urllib3's ProxyManager.

For users with custom adapters, this _may_ be potentially breaking if you were already working around this behavior. The previous functionality of `rebuild_proxies` doesn't make sense in any case, so we would encourage any users impacted to migrate any handling of Proxy-Authorization directly into their custom adapter.

### Workarounds
For users who are not able to update Requests immediately, there is one potential workaround.

You may disable redirects by setting `allow_redirects` to `False` on all calls through Requests top-level APIs. Note that if you're currently relying on redirect behaviors, you will need to capture the 3xx response codes and ensure a new request is made to the redirect destination.
```
import requests
r = requests.get('http://github.com/', allow_redirects=False)
```

### Credits

This vulnerability was discovered and disclosed by the following individuals.

Dennis Brinkrolf, Haxolot (https://haxolot.com/)
Tobias Funke, (tobiasfunke93@gmail.com)

## References

- https://github.com/psf/requests/security/advisories/GHSA-j8r2-6x86-q33q
- https://nvd.nist.gov/vuln/detail/CVE-2023-32681
- https://github.com/psf/requests/commit/74ea7cf7a6a27a4eeb2ae24e162bcc942a6706d5
- https://github.com/psf/requests
- https://github.com/psf/requests/releases/tag/v2.31.0
- https://github.com/pypa/advisory-database/tree/main/vulns/requests/PYSEC-2023-74.yaml
- https://lists.debian.org/debian-lts-announce/2023/06/msg00018.html
- https://lists.fedoraproject.org/archives/list/package-announce@lists.fedoraproject.org/message/AW7HNFGYP44RT3DUDQXG2QT3OEV2PJ7Y
- https://lists.fedoraproject.org/archives/list/package-announce@lists.fedoraproject.org/message/KOYASTZDGQG2BWLSNBPL3TQRL2G7QYNZ
- https://security.gentoo.org/glsa/202309-08

---
Source: the GitHub Advisory Database, CC BY 4.0. https://github.com/advisories/GHSA-j8r2-6x86-q33q
