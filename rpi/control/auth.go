package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"log"
	"net/http"
	"strings"
	"time"
)

const sessionCookieName = "x360_netdrive_session"
const loginCookieName = "x360_netdrive_login"
const sessionLifetime = 12 * time.Hour
const maxSessions = 32

func (c *controller) validCredentials(user, token string) bool {
	hash := sha256.Sum256([]byte(token))
	return subtle.ConstantTimeCompare(hash[:], c.tokenHash[:]) == 1 && user == "admin"
}

func (c *controller) authenticated(r *http.Request) bool {
	// Retain Basic authentication for existing command-line clients, without a browser challenge.
	if user, token, ok := r.BasicAuth(); ok && c.validCredentials(user, token) {
		return true
	}
	cookie, err := r.Cookie(sessionCookieName)
	if err != nil {
		return false
	}
	key := sha256.Sum256([]byte(cookie.Value))
	c.sessionsMu.Lock()
	defer c.sessionsMu.Unlock()
	expiry, ok := c.sessions[key]
	if !ok || !time.Now().Before(expiry) {
		delete(c.sessions, key)
		return false
	}
	return true
}

func validForm(w http.ResponseWriter, r *http.Request, csrf string) bool {
	r.Body = http.MaxBytesReader(w, r.Body, 8192)
	if err := r.ParseForm(); err != nil {
		http.Error(w, "Invalid or oversized form", http.StatusBadRequest)
		return false
	}
	origin := r.Header.Get("Origin")
	// TLS can terminate at a proxy that preserves the public Host header.
	validOrigin := origin == "" || origin == "http://"+r.Host || origin == "https://"+r.Host
	if origin == "null" {
		// Embedded browsers can use opaque origins. Still require a cookie-bound CSRF token.
		_, sessionErr := r.Cookie(sessionCookieName)
		_, loginErr := r.Cookie(loginCookieName)
		validOrigin = sessionErr == nil || loginErr == nil
	}
	if !validOrigin ||
		subtle.ConstantTimeCompare([]byte(r.PostForm.Get("csrf")), []byte(csrf)) != 1 {
		http.Error(w, "Invalid request origin or CSRF token. Refresh the page.", http.StatusForbidden)
		return false
	}
	return true
}

func sessionCookie(r *http.Request) *http.Cookie {
	return &http.Cookie{
		Name: sessionCookieName, Path: "/", HttpOnly: true, SameSite: http.SameSiteStrictMode,
		Secure: r.TLS != nil || r.Header.Get("Origin") == "https://"+r.Host,
	}
}

func (c *controller) signedLoginChallenge(nonce string) string {
	mac := hmac.New(sha256.New, []byte(c.loginCSRF))
	mac.Write([]byte(nonce))
	return nonce + "." + hex.EncodeToString(mac.Sum(nil))
}

func (c *controller) loginChallenge(r *http.Request) string {
	cookie, err := r.Cookie(loginCookieName)
	if err != nil {
		return ""
	}
	nonce, _, ok := strings.Cut(cookie.Value, ".")
	if !ok || len(nonce) != 64 || !hmac.Equal([]byte(cookie.Value), []byte(c.signedLoginChallenge(nonce))) {
		return ""
	}
	return cookie.Value
}

func (c *controller) renderLogin(w http.ResponseWriter, r *http.Request, status int, message string) {
	challenge := c.loginChallenge(r)
	if challenge == "" {
		nonce := make([]byte, 32)
		if _, err := rand.Read(nonce); err != nil {
			log.Printf("create sign-in challenge: %v", err)
			http.Error(w, "Unable to prepare sign-in. Refresh the page.", http.StatusInternalServerError)
			return
		}
		challenge = c.signedLoginChallenge(hex.EncodeToString(nonce))
	}
	cookie := sessionCookie(r)
	cookie.Name, cookie.Value, cookie.MaxAge = loginCookieName, challenge, 600
	http.SetCookie(w, cookie)
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.WriteHeader(status)
	if err := c.page.ExecuteTemplate(w, "login.html", pageData{CSRF: challenge, Error: message}); err != nil {
		log.Printf("render sign-in page: %v", err)
	}
}

func (c *controller) login(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		if c.authenticated(r) {
			http.Redirect(w, r, "/", http.StatusSeeOther)
		} else {
			c.renderLogin(w, r, http.StatusOK, "")
		}
		return
	case http.MethodPost:
		challenge := c.loginChallenge(r)
		if challenge == "" {
			http.Error(w, "Sign-in cookie missing or invalid. Refresh the page and allow cookies.", http.StatusForbidden)
			return
		}
		if !validForm(w, r, challenge) {
			return
		}
	default:
		w.Header().Set("Allow", "GET, POST")
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !c.validCredentials(r.PostForm.Get("username"), r.PostForm.Get("password")) {
		c.renderLogin(w, r, http.StatusUnauthorized, "Incorrect username or admin token. Try again.")
		return
	}
	nonce := make([]byte, 32)
	if _, err := rand.Read(nonce); err != nil {
		log.Printf("create sign-in session: %v", err)
		http.Error(w, "Unable to create a session. Retry signing in.", http.StatusInternalServerError)
		return
	}
	value := hex.EncodeToString(nonce)
	expiry := time.Now().Add(sessionLifetime)
	c.sessionsMu.Lock()
	if old, err := r.Cookie(sessionCookieName); err == nil {
		delete(c.sessions, sha256.Sum256([]byte(old.Value)))
	}
	var oldest [32]byte
	var oldestExpiry time.Time
	for key, until := range c.sessions {
		if !time.Now().Before(until) {
			delete(c.sessions, key)
		} else if oldestExpiry.IsZero() || until.Before(oldestExpiry) {
			oldest, oldestExpiry = key, until
		}
	}
	if len(c.sessions) >= maxSessions {
		delete(c.sessions, oldest)
	}
	c.sessions[sha256.Sum256([]byte(value))] = expiry
	c.sessionsMu.Unlock()
	cookie := sessionCookie(r)
	cookie.Value, cookie.Expires, cookie.MaxAge = value, expiry, int(sessionLifetime.Seconds())
	http.SetCookie(w, cookie)
	http.Redirect(w, r, "/", http.StatusSeeOther)
}

func (c *controller) logout(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		w.Header().Set("Allow", "POST")
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !validForm(w, r, c.csrf) {
		return
	}
	if cookie, err := r.Cookie(sessionCookieName); err == nil {
		c.sessionsMu.Lock()
		delete(c.sessions, sha256.Sum256([]byte(cookie.Value)))
		c.sessionsMu.Unlock()
	}
	cookie := sessionCookie(r)
	cookie.MaxAge, cookie.Expires = -1, time.Unix(1, 0)
	http.SetCookie(w, cookie)
	http.Redirect(w, r, "/", http.StatusSeeOther)
}
