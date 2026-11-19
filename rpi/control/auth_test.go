package main

import (
	"context"
	"crypto/sha256"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

func loginRequest(c *controller, address, username, password, csrf, origin string) *http.Request {
	challenge := c.signedLoginChallenge(strings.Repeat("a", 64))
	if csrf == c.loginCSRF {
		csrf = challenge
	}
	form := url.Values{"username": {username}, "password": {password}, "csrf": {csrf}}
	req := httptest.NewRequest(http.MethodPost, address+"/login", strings.NewReader(form.Encode()))
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.Header.Set("Origin", origin)
	req.AddCookie(&http.Cookie{Name: loginCookieName, Value: challenge})
	return req
}

func loginForTest(t *testing.T, c *controller, address string) *http.Cookie {
	t.Helper()
	rec := httptest.NewRecorder()
	c.ServeHTTP(rec, loginRequest(c, address, "admin", testToken, c.loginCSRF, address))
	if rec.Code != http.StatusSeeOther || rec.Header().Get("Location") != "/" {
		t.Fatalf("login did not redirect to controls: %d %s", rec.Code, rec.Body.String())
	}
	cookies := rec.Result().Cookies()
	if len(cookies) != 1 || cookies[0].Name != sessionCookieName {
		t.Fatalf("login did not issue one session cookie: %v", cookies)
	}
	return cookies[0]
}

func TestHTMLLoginAuthenticatesAndLogoutRevokesSession(t *testing.T) {
	calls := 0
	c := testController(t, func(_ context.Context, name string, _ ...string) (string, error) {
		calls++
		if name == "journalctl" {
			return "test log", nil
		}
		return "ActiveState=active\nSubState=exited\nResult=success", nil
	})
	page := func(cookie *http.Cookie) *httptest.ResponseRecorder {
		t.Helper()
		req := httptest.NewRequest(http.MethodGet, "http://pi.test/", nil)
		if cookie != nil {
			req.AddCookie(cookie)
		}
		rec := httptest.NewRecorder()
		c.ServeHTTP(rec, req)
		return rec
	}
	before := page(nil)
	if before.Code != http.StatusOK || !strings.Contains(before.Body.String(), `action="/login"`) ||
		before.Header().Get("WWW-Authenticate") != "" || strings.Contains(before.Body.String(), c.csrf) || calls != 0 {
		t.Fatal("anonymous browser did not get an isolated HTML login form")
	}
	cookie := loginForTest(t, c, "http://pi.test")
	if !cookie.HttpOnly || cookie.SameSite != http.SameSiteStrictMode || cookie.Secure ||
		cookie.Path != "/" || cookie.MaxAge != 43200 || cookie.Value == testToken {
		t.Fatalf("incorrect LAN session cookie attributes: %+v", cookie)
	}
	after := page(cookie)
	if after.Code != 200 || !strings.Contains(after.Body.String(), "Saved servers") ||
		strings.Contains(after.Body.String(), testToken) || calls == 0 {
		t.Fatal("session cookie did not authenticate the controls without revealing the token")
	}
	logout := httptest.NewRequest(http.MethodPost, "http://pi.test/logout",
		strings.NewReader(url.Values{"csrf": {c.csrf}}.Encode()))
	logout.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	logout.AddCookie(cookie)
	rec := httptest.NewRecorder()
	c.ServeHTTP(rec, logout)
	if rec.Code != http.StatusSeeOther || rec.Result().Cookies()[0].MaxAge != -1 {
		t.Fatal("logout did not redirect and clear the browser cookie")
	}
	calls = 0
	if !strings.Contains(page(cookie).Body.String(), `action="/login"`) || calls != 0 {
		t.Fatal("logged-out session could still access controls")
	}
}

func TestHTMLLoginRejectsInvalidCredentialsCSRFAndOrigin(t *testing.T) {
	c := testController(t, func(context.Context, string, ...string) (string, error) {
		t.Fatal("failed login reached system commands")
		return "", nil
	})
	for _, tc := range []struct {
		name, user, token, csrf, origin string
		status                          int
	}{
		{"wrong-user", "other", testToken, c.loginCSRF, "http://pi.test", 401},
		{"wrong-password", "admin", "incorrect", c.loginCSRF, "http://pi.test", 401},
		{"empty-password", "admin", "", c.loginCSRF, "http://pi.test", 401},
		{"missing-csrf", "admin", testToken, "", "http://pi.test", 403},
		{"control-csrf", "admin", testToken, c.csrf, "http://pi.test", 403},
		{"foreign-origin", "admin", testToken, c.loginCSRF, "https://foreign.test", 403},
		{"opaque-origin-bad-password", "admin", "incorrect", c.loginCSRF, "null", 401},
	} {
		t.Run(tc.name, func(t *testing.T) {
			rec := httptest.NewRecorder()
			c.ServeHTTP(rec, loginRequest(c, "http://pi.test", tc.user, tc.token, tc.csrf, tc.origin))
			if rec.Code != tc.status ||
				strings.Contains(rec.Body.String(), testToken) || rec.Header().Get("WWW-Authenticate") != "" {
				t.Fatalf("unsafe login response: %d %s", rec.Code, rec.Body.String())
			}
			for _, cookie := range rec.Result().Cookies() {
				if cookie.Name == sessionCookieName {
					t.Fatal("failed sign-in issued an authenticated session")
				}
			}
			if tc.status == 401 && !strings.Contains(rec.Body.String(), "Incorrect username or admin token") {
				t.Fatal("invalid credentials did not show a usable form error")
			}
		})
	}
}

func TestOpaqueOriginLoginRequiresCookieBoundChallenge(t *testing.T) {
	c := testController(t, nil)
	initial := httptest.NewRecorder()
	c.ServeHTTP(initial, httptest.NewRequest(http.MethodGet, "http://pi.test/", nil))
	cookie := initial.Result().Cookies()[0]
	if cookie.Name != loginCookieName || !cookie.HttpOnly ||
		!strings.Contains(initial.Body.String(), `value="`+cookie.Value+`"`) ||
		strings.Contains(initial.Body.String(), c.loginCSRF) {
		t.Fatal("login form did not issue a browser-bound challenge or leaked its signing key")
	}
	for _, tc := range []struct {
		name, cookie, csrf string
		status             int
	}{
		{"matching", cookie.Value, cookie.Value, 303},
		{"missing-cookie", "", cookie.Value, 403},
		{"forged-cookie", "forged", "forged", 403},
		{"another-browser-token", cookie.Value, c.signedLoginChallenge(strings.Repeat("b", 64)), 403},
		{"changed-signature", cookie.Value + "x", cookie.Value + "x", 403},
	} {
		t.Run(tc.name, func(t *testing.T) {
			req := loginRequest(c, "http://pi.test", "admin", testToken, tc.csrf, "null")
			req.Header.Del("Cookie")
			if tc.cookie != "" {
				req.AddCookie(&http.Cookie{Name: loginCookieName, Value: tc.cookie})
			}
			rec := httptest.NewRecorder()
			c.ServeHTTP(rec, req)
			if rec.Code != tc.status {
				t.Fatalf("cookie-bound sign-in: status=%d, want %d", rec.Code, tc.status)
			}
		})
	}
}

func TestSessionsExpireRejectTamperingAndUseSecureCookiesOnHTTPS(t *testing.T) {
	c := testController(t, nil)
	for _, address := range []string{"https://pi.test", "http://pi.test"} {
		cookie := loginForTest(t, c, address)
		if cookie.Secure != strings.HasPrefix(address, "https:") {
			t.Fatalf("wrong Secure attribute for %s", address)
		}
		req := httptest.NewRequest(http.MethodGet, address+"/", nil)
		req.AddCookie(cookie)
		if !c.authenticated(req) {
			t.Fatal("fresh session did not authenticate")
		}
		tampered := *cookie
		tampered.Value += "tampered"
		bad := httptest.NewRequest(http.MethodGet, address+"/", nil)
		bad.AddCookie(&tampered)
		if c.authenticated(bad) {
			t.Fatal("tampered session authenticated")
		}
		if restarted := testController(t, nil); restarted.authenticated(req) {
			t.Fatal("session survived a control service restart")
		}
		key := sha256.Sum256([]byte(cookie.Value))
		c.sessions[key] = time.Now().Add(-time.Second)
		if c.authenticated(req) {
			t.Fatal("expired session authenticated")
		}
	}

	req := loginRequest(c, "http://pi.test", "admin", testToken, c.loginCSRF, "https://pi.test")
	rec := httptest.NewRecorder()
	c.ServeHTTP(rec, req)
	if rec.Code != 303 || !rec.Result().Cookies()[0].Secure {
		t.Fatal("HTTPS reverse-proxy sign-in did not set a Secure cookie")
	}
}

func TestRepeatedLoginReplacesSessionAndBoundsSessionStorage(t *testing.T) {
	c := testController(t, nil)
	first := loginForTest(t, c, "http://pi.test")
	req := loginRequest(c, "http://pi.test", "admin", testToken, c.loginCSRF, "http://pi.test")
	req.AddCookie(first)
	rec := httptest.NewRecorder()
	c.ServeHTTP(rec, req)
	if rec.Code != 303 || len(c.sessions) != 1 || c.authenticated(req) ||
		rec.Result().Cookies()[0].Value == first.Value {
		t.Fatal("repeated login did not replace the previous session")
	}
	oldest := rec.Result().Cookies()[0]
	c.sessions[sha256.Sum256([]byte(oldest.Value))] = time.Now().Add(time.Hour)
	for i := 0; i < maxSessions; i++ {
		loginForTest(t, c, "http://pi.test")
	}
	req = httptest.NewRequest(http.MethodGet, "http://pi.test/", nil)
	req.AddCookie(oldest)
	if len(c.sessions) != maxSessions || c.authenticated(req) {
		t.Fatal("session storage exceeded its bound or did not evict the oldest session")
	}
}

func TestCookieActionsAndLogoutRequirePrivateCSRFTokens(t *testing.T) {
	calls := 0
	c := testController(t, func(context.Context, string, ...string) (string, error) {
		calls++
		return "ActiveState=active\nSubState=exited\nResult=success", nil
	})
	cookie := loginForTest(t, c, "http://pi.test")
	for _, path := range []string{"/", "/logout"} {
		for _, csrf := range []string{"", c.loginCSRF} {
			req := httptest.NewRequest(http.MethodPost, "http://pi.test"+path,
				strings.NewReader(url.Values{"csrf": {csrf}, "action": {"detach"}, "confirm": {"yes"}}.Encode()))
			req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			req.AddCookie(cookie)
			rec := httptest.NewRecorder()
			c.ServeHTTP(rec, req)
			if rec.Code != http.StatusForbidden || !c.authenticated(req) || calls != 0 {
				t.Fatal("public/missing CSRF token affected a signed-in session or USB")
			}
		}
	}
	req := httptest.NewRequest(http.MethodGet, "http://pi.test/logout", nil)
	req.AddCookie(cookie)
	rec := httptest.NewRecorder()
	c.ServeHTTP(rec, req)
	if rec.Code != http.StatusMethodNotAllowed || !c.authenticated(req) {
		t.Fatal("GET could sign out a session")
	}
	req = httptest.NewRequest(http.MethodPost, "http://pi.test/",
		strings.NewReader(url.Values{"csrf": {c.csrf}, "action": {"detach"}, "confirm": {"yes"}}.Encode()))
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.AddCookie(cookie)
	rec = httptest.NewRecorder()
	c.ServeHTTP(rec, req)
	if rec.Code != http.StatusSeeOther || calls != 1 {
		t.Fatal("valid session and private CSRF token could not reach the existing control action")
	}
}
