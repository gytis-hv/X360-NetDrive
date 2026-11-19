package main

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"reflect"
	"strings"
	"testing"
)

const testToken = "test-only-admin-token-0123456789abcdef"

func testController(t *testing.T, run commandRunner) *controller {
	t.Helper()
	c, err := newController(testToken, t.TempDir(), run)
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func profileFixture(address string) settings {
	return settings{
		Version: 1,
		Servers: []savedServer{
			{ID: "1111111111111111", Name: "First server", URL: address},
			{ID: "2222222222222222", Name: "Second server", URL: address + "/second"},
		},
		Slots: []string{"1111111111111111", "", "", "", "", "", "", ""},
	}
}

func manifestFixture(t *testing.T) *httptest.Server {
	t.Helper()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte(`{"disk":"/exports/123456789012345678901234/disk","diskBytes":8192,"readOnly":true}`))
	}))
	t.Cleanup(server.Close)
	return server
}

func TestSavedServersPersistAcrossControllerRestart(t *testing.T) {
	c := testController(t, nil)
	for _, form := range []url.Values{
		{"action": {"add"}, "name": {"Library"}, "url": {"http://library.test"}},
		{"action": {"add"}, "name": {"NAS"}, "url": {"http://nas.test/games"}},
	} {
		if err := c.action(context.Background(), form); err != nil {
			t.Fatal(err)
		}
	}
	restarted := testController(t, nil)
	restarted.configFile = c.configFile
	s, exists, err := loadSettings(restarted.configFile)
	if err != nil || !exists || len(s.Servers) != 2 {
		t.Fatalf("profiles were not restored: %+v, %v", s, err)
	}
	if s.Servers[0].Name != "Library" || s.Servers[1].URL != "http://nas.test/games" {
		t.Fatalf("profile content changed: %+v", s.Servers)
	}
}

func TestMountSwitchesSavedServerOnFixedUSBDrive(t *testing.T) {
	server := manifestFixture(t)
	var calls []string
	c := testController(t, func(_ context.Context, name string, args ...string) (string, error) {
		if name == "systemctl" && len(args) > 0 && args[0] == "show" {
			return "ActiveState=active\nSubState=exited\nResult=success", nil
		}
		if name == "journalctl" {
			return "", nil
		}
		calls = append(calls, name+" "+strings.Join(args, " "))
		return "", nil
	})
	legacy := profileFixture(server.URL)
	legacy.Slots[7] = legacy.Servers[0].ID
	if err := saveSettings(c.configFile, legacy); err != nil {
		t.Fatal(err)
	}
	page := httptest.NewRecorder()
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.SetBasicAuth("admin", testToken)
	c.ServeHTTP(page, req)
	if page.Code != 200 || !strings.Contains(page.Body.String(), "choose one saved server") {
		t.Fatalf("legacy conflict did not remain recoverable in the page: %d %s", page.Code, page.Body.String())
	}
	err := c.action(context.Background(), url.Values{
		"action": {"mount"}, "server": {"2222222222222222"}, "confirm": {"yes"},
	})
	if err != nil {
		t.Fatal(err)
	}
	s, _, err := loadSettings(c.configFile)
	if err != nil || s.Slots[0] != "2222222222222222" || strings.Join(s.Slots[1:], "") != "" || len(s.Servers) != 2 {
		t.Fatalf("switch did not keep one fixed assignment and both profiles: %+v, %v", s, err)
	}
	want := []string{"systemctl stop " + usbUnit, "systemctl stop " + sourceUnit, "systemctl start --no-block " + usbUnit}
	if !reflect.DeepEqual(calls, want) {
		t.Fatalf("unsafe reconnect sequence: %v", calls)
	}
}

func TestUnmountClearsLegacyAssignmentsButKeepsSavedServers(t *testing.T) {
	server := manifestFixture(t)
	c := testController(t, func(context.Context, string, ...string) (string, error) { return "", nil })
	s := profileFixture(server.URL)
	s.Slots[7] = s.Servers[1].ID
	if err := saveSettings(c.configFile, s); err != nil {
		t.Fatal(err)
	}
	if err := c.action(context.Background(), url.Values{"action": {"unmount"}, "confirm": {"yes"}}); err != nil {
		t.Fatal(err)
	}
	s, _, err := loadSettings(c.configFile)
	if err != nil || strings.Join(s.Slots, "") != "" || len(s.Servers) != 2 {
		t.Fatalf("unmount did not clear assignments and preserve profiles: %+v, %v", s, err)
	}
}

func TestLastUnmountPersistsEmptySlotsWithoutAttachingUSB(t *testing.T) {
	var calls []string
	c := testController(t, func(_ context.Context, _ string, args ...string) (string, error) {
		calls = append(calls, strings.Join(args, " "))
		return "", nil
	})
	if err := saveSettings(c.configFile, profileFixture("http://unused.test")); err != nil {
		t.Fatal(err)
	}
	if err := c.action(context.Background(), url.Values{"action": {"unmount"}, "confirm": {"yes"}}); err != nil {
		t.Fatal(err)
	}
	s, _, err := loadSettings(c.configFile)
	if err != nil || strings.Join(s.Slots, "") != "" || len(calls) != 2 {
		t.Fatalf("empty assignment was reattached: %+v %v %v", s, calls, err)
	}
}

func TestMountPreflightFailureLeavesAttachedUSBAndSettingsUntouched(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
	}))
	defer server.Close()
	c := testController(t, func(context.Context, string, ...string) (string, error) {
		t.Fatal("failed discovery interrupted USB")
		return "", nil
	})
	if err := saveSettings(c.configFile, profileFixture(server.URL)); err != nil {
		t.Fatal(err)
	}
	before, err := os.ReadFile(c.configFile)
	if err != nil {
		t.Fatal(err)
	}
	err = c.action(context.Background(), url.Values{
		"action": {"mount"}, "server": {"2222222222222222"}, "confirm": {"yes"},
	})
	after, readErr := os.ReadFile(c.configFile)
	if err == nil || readErr != nil || string(before) != string(after) {
		t.Fatalf("failed preflight changed settings: %v, %v", err, readErr)
	}
}

func TestUSBStopFailureDoesNotStopSourcesOrSaveAssignments(t *testing.T) {
	server := manifestFixture(t)
	var calls []string
	c := testController(t, func(_ context.Context, _ string, args ...string) (string, error) {
		calls = append(calls, strings.Join(args, " "))
		return "", errors.New("USB is busy")
	})
	if err := saveSettings(c.configFile, profileFixture(server.URL)); err != nil {
		t.Fatal(err)
	}
	err := c.action(context.Background(), url.Values{
		"action": {"mount"}, "server": {"2222222222222222"}, "confirm": {"yes"},
	})
	s, _, readErr := loadSettings(c.configFile)
	if err == nil || readErr != nil || s.Slots[7] != "" ||
		!reflect.DeepEqual(calls, []string{"stop " + usbUnit}) {
		t.Fatalf("USB failure changed sources or settings: %v, %+v, %v", calls, s, err)
	}
}

func TestStaleSlotRequestsAndMissingConfirmationDoNotTouchServices(t *testing.T) {
	c := testController(t, func(context.Context, string, ...string) (string, error) {
		t.Fatal("invalid mapping reached system commands")
		return "", nil
	})
	if err := saveSettings(c.configFile, profileFixture("http://unused.test")); err != nil {
		t.Fatal(err)
	}
	for _, form := range []url.Values{
		{"action": {"mount"}, "server": {"2222222222222222"}, "slot": {"0"}, "confirm": {"yes"}},
		{"action": {"mount"}, "server": {"2222222222222222"}, "slot": {"9"}, "confirm": {"yes"}},
		{"action": {"mount"}, "server": {"2222222222222222"}, "slot": {"1"}, "confirm": {"yes"}},
		{"action": {"mount"}, "server": {"2222222222222222"}},
		{"action": {"unmount"}},
		{"action": {"mount"}, "server": {"unknown"}, "confirm": {"yes"}},
		{"action": {"mount"}, "confirm": {"yes"}},
	} {
		if err := c.action(context.Background(), form); err == nil {
			t.Fatal("unsafe mapping request accepted")
		}
	}
}

func TestDeleteRequiresUnmountAndPreservesUnrelatedProfiles(t *testing.T) {
	c := testController(t, nil)
	if err := saveSettings(c.configFile, profileFixture("http://unused.test")); err != nil {
		t.Fatal(err)
	}
	if err := c.action(context.Background(), url.Values{"action": {"remove"}, "server": {"1111111111111111"}}); err == nil {
		t.Fatal("deleted an assigned server")
	}
	if err := c.action(context.Background(), url.Values{"action": {"remove"}, "server": {"2222222222222222"}}); err != nil {
		t.Fatal(err)
	}
	s, _, err := loadSettings(c.configFile)
	if err != nil || len(s.Servers) != 1 || s.Servers[0].ID != "1111111111111111" || s.Slots[0] != s.Servers[0].ID {
		t.Fatalf("deletion changed another profile or assignment: %+v %v", s, err)
	}
}

func TestDetachOnlyLeavesSourceAndAssignmentsIntact(t *testing.T) {
	var calls []string
	c := testController(t, func(_ context.Context, _ string, args ...string) (string, error) {
		calls = append(calls, strings.Join(args, " "))
		return "", nil
	})
	s := profileFixture("http://unused.test")
	if err := saveSettings(c.configFile, s); err != nil {
		t.Fatal(err)
	}
	if err := c.action(context.Background(), url.Values{"action": {"detach"}, "confirm": {"yes"}}); err != nil {
		t.Fatal(err)
	}
	after, _, err := loadSettings(c.configFile)
	if err != nil || !reflect.DeepEqual(s, after) || !reflect.DeepEqual(calls, []string{"stop " + usbUnit}) {
		t.Fatalf("temporary detach changed persistent state: %+v, %v", after, calls)
	}
}

func TestAttachRequiresActiveSourceAndNonemptyAssignments(t *testing.T) {
	calls := 0
	c := testController(t, func(context.Context, string, ...string) (string, error) {
		calls++
		return "ActiveState=activating", nil
	})
	if err := c.action(context.Background(), url.Values{"action": {"attach"}}); err == nil || calls != 1 {
		t.Fatal("USB attached before source readiness")
	}
	if err := saveSettings(c.configFile, settings{Version: 1, Servers: []savedServer{}, Slots: make([]string, 8)}); err != nil {
		t.Fatal(err)
	}
	if err := c.action(context.Background(), url.Values{"action": {"attach"}}); err == nil || calls != 1 {
		t.Fatal("USB attached with no assigned server")
	}
}

func TestHTTPAuthenticationCSRFAndOriginProtectMutations(t *testing.T) {
	c := testController(t, func(context.Context, string, ...string) (string, error) {
		t.Fatal("unauthorized request reached system commands")
		return "", nil
	})
	for _, tc := range []struct {
		name, token, csrf, origin string
		status                    int
	}{
		{"anonymous", "", "", "", http.StatusUnauthorized},
		{"wrong-token", "incorrect", c.csrf, "", http.StatusUnauthorized},
		{"missing-csrf", testToken, "", "", http.StatusForbidden},
		{"cross-origin", testToken, c.csrf, "http://malicious.test", http.StatusForbidden},
		{"opaque-origin", testToken, c.csrf, "null", http.StatusForbidden},
	} {
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequest(http.MethodPost, "http://pi.test/", strings.NewReader(
				url.Values{"action": {"detach"}, "confirm": {"yes"}, "csrf": {tc.csrf}}.Encode()))
			req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			req.Header.Set("Origin", tc.origin)
			if tc.token != "" {
				req.SetBasicAuth("admin", tc.token)
			}
			rec := httptest.NewRecorder()
			c.ServeHTTP(rec, req)
			if rec.Code != tc.status {
				t.Fatalf("unsafe request accepted: status=%d", rec.Code)
			}
		})
	}
}

func TestAuthenticatedPageEscapesSavedNamesAndLogsWithoutExposingToken(t *testing.T) {
	c := testController(t, func(_ context.Context, name string, _ ...string) (string, error) {
		if name == "journalctl" {
			return "<script>alert('log')</script>", nil
		}
		return "ActiveState=active\nSubState=exited\nResult=success", nil
	})
	s := profileFixture("http://unused.test")
	s.Servers[0].Name = "<script>profile</script>"
	if err := saveSettings(c.configFile, s); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodGet, "/", nil)
	req.SetBasicAuth("admin", testToken)
	rec := httptest.NewRecorder()
	c.ServeHTTP(rec, req)
	body := rec.Body.String()
	if rec.Code != 200 || !strings.Contains(body, "&lt;script&gt;profile&lt;/script&gt;") ||
		!strings.Contains(body, "&lt;script&gt;alert") || strings.Contains(body, "<script>") ||
		strings.Contains(body, testToken) || strings.Contains(body, "name=\"mode\"") ||
		strings.Contains(body, "name=\"slot\"") || strings.Contains(body, "<select") ||
		!strings.Contains(body, "Mount server") || !strings.Contains(body, "LUN0") {
		t.Fatalf("unsafe or outdated page: status=%d", rec.Code)
	}
}

func TestValidMutationRedirectsAndBackendFailuresAreVisible(t *testing.T) {
	for _, fail := range []bool{false, true} {
		t.Run(map[bool]string{false: "success", true: "backend-failure"}[fail], func(t *testing.T) {
			c := testController(t, func(_ context.Context, _ string, args ...string) (string, error) {
				if len(args) > 0 && args[0] == "stop" && fail {
					return "", errors.New("stop command failed")
				}
				return "ActiveState=inactive\nSubState=dead\nResult=success", nil
			})
			req := httptest.NewRequest(http.MethodPost, "http://pi.test/", strings.NewReader(
				url.Values{"action": {"detach"}, "confirm": {"yes"}, "csrf": {c.csrf}}.Encode()))
			req.SetBasicAuth("admin", testToken)
			req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
			req.Header.Set("Origin", "https://pi.test")
			rec := httptest.NewRecorder()
			c.ServeHTTP(rec, req)
			if fail {
				if rec.Code != http.StatusBadRequest || !strings.Contains(rec.Body.String(), "stop command failed") {
					t.Fatal("backend failure was hidden")
				}
			} else if rec.Code != http.StatusSeeOther || rec.Header().Get("Location") != "/?requested=1" {
				t.Fatal("successful POST did not redirect")
			}
		})
	}
}
