package main

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func serversTestSettings(firstURL, secondURL string) settings {
	firstID := "0123456789abcdef"
	secondID := "fedcba9876543210"
	return settings{
		Version: 1,
		Servers: []savedServer{
			{ID: firstID, Name: "Primary", URL: firstURL},
			{ID: secondID, Name: "Backup", URL: secondURL},
		},
		Slots: []string{"", firstID, "", "", "", "", "", secondID},
	}
}

func TestSaveAndLoadPreservesLegacyProfilesAndAssignments(t *testing.T) {
	want := serversTestSettings("https://primary.example", "https://backup.example")
	path := filepath.Join(t.TempDir(), "servers.json")

	if err := saveSettings(path, want); err != nil {
		t.Fatalf("saveSettings() error = %v", err)
	}
	got, exists, err := loadSettings(path)
	if err != nil {
		t.Fatalf("loadSettings() error = %v", err)
	}
	if !exists {
		t.Fatal("loadSettings() exists = false, want true")
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("loadSettings() = %#v, want %#v", got, want)
	}
}

func TestLoadSettingsRejectsMalformedDocumentsWithoutChangingThem(t *testing.T) {
	eightEmptySlots := `["","","","","","","",""]`
	first := `{"id":"0123456789abcdef","name":"Primary","url":"https://primary.example"}`
	second := `{"id":"fedcba9876543210","name":"Backup","url":"https://backup.example"}`
	cases := []struct {
		name string
		data string
	}{
		{name: "invalid_json", data: `{"version":`},
		{name: "missing_version", data: `{"servers":[],"slots":` + eightEmptySlots + `}`},
		{name: "missing_servers", data: `{"version":1,"slots":` + eightEmptySlots + `}`},
		{name: "missing_slots", data: `{"version":1,"servers":[]}`},
		{name: "wrong_version", data: `{"version":2,"servers":[],"slots":` + eightEmptySlots + `}`},
		{name: "wrong_slot_count", data: `{"version":1,"servers":[],"slots":["","","","","","",""]}`},
		{name: "unknown_server_slot", data: `{"version":1,"servers":[],"slots":["0123456789abcdef","","","","","","",""]}`},
		{name: "duplicate_server_ids", data: `{"version":1,"servers":[` + first + `,` + strings.Replace(second, "fedcba9876543210", "0123456789abcdef", 1) + `],"slots":` + eightEmptySlots + `}`},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "servers.json")
			before := []byte(tc.data)
			if err := os.WriteFile(path, before, 0600); err != nil {
				t.Fatalf("write malformed settings: %v", err)
			}

			_, exists, err := loadSettings(path)
			if err == nil {
				t.Fatal("loadSettings() error = nil, want explicit validation failure")
			}
			if !exists {
				t.Fatal("loadSettings() exists = false, want true for a present invalid file")
			}
			after, readErr := os.ReadFile(path)
			if readErr != nil {
				t.Fatalf("read settings after load: %v", readErr)
			}
			if !reflect.DeepEqual(after, before) {
				t.Fatalf("loadSettings() changed invalid file to %q, want original bytes %q", after, before)
			}
		})
	}
}

func TestSaveSettingsRejectsInvalidSettingsWithoutReplacingValidFile(t *testing.T) {
	valid := serversTestSettings("https://primary.example", "https://backup.example")
	cases := []struct {
		name string
		edit func(*settings)
	}{
		{
			name: "unknown_slot",
			edit: func(s *settings) { s.Slots[0] = "aaaaaaaaaaaaaaaa" },
		},
		{
			name: "duplicate_id",
			edit: func(s *settings) { s.Servers[1].ID = s.Servers[0].ID },
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			path := filepath.Join(t.TempDir(), "servers.json")
			if err := saveSettings(path, valid); err != nil {
				t.Fatalf("save initial settings: %v", err)
			}
			before, err := os.ReadFile(path)
			if err != nil {
				t.Fatalf("read initial settings: %v", err)
			}
			invalid := valid
			invalid.Servers = append([]savedServer(nil), valid.Servers...)
			invalid.Slots = append([]string(nil), valid.Slots...)
			tc.edit(&invalid)

			if err := saveSettings(path, invalid); err == nil {
				t.Fatal("saveSettings() error = nil, want validation failure")
			}
			after, err := os.ReadFile(path)
			if err != nil {
				t.Fatalf("read settings after failed save: %v", err)
			}
			if !reflect.DeepEqual(after, before) {
				t.Fatalf("failed save replaced valid settings: got %q, want %q", after, before)
			}
		})
	}
}

func TestSaveSettingsIOFailurePreservesExistingFile(t *testing.T) {
	blockingFile := filepath.Join(t.TempDir(), "not-a-directory")
	priorData := []byte("prior file data")
	if err := os.WriteFile(blockingFile, priorData, 0600); err != nil {
		t.Fatalf("create prior file: %v", err)
	}
	settingsPath := filepath.Join(blockingFile, "servers.json")

	err := saveSettings(settingsPath, serversTestSettings("https://primary.example", "https://backup.example"))
	if err == nil {
		t.Fatal("saveSettings() error = nil, want filesystem write failure")
	}
	got, readErr := os.ReadFile(blockingFile)
	if readErr != nil {
		t.Fatalf("read prior file after failed save: %v", readErr)
	}
	if !reflect.DeepEqual(got, priorData) {
		t.Fatalf("prior file after failed save = %q, want %q", got, priorData)
	}
}

func TestDiscoverSourceAcceptsCurrentReadOnlyDiskManifest(t *testing.T) {
	const diskPath = "/exports/0123456789abcdef01234567/disk"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json; charset=utf-8")
		_, _ = fmt.Fprintf(w, `{"readOnly":true,"diskBytes":4096,"disk":%q}`, diskPath)
	}))
	defer server.Close()

	got, err := discoverSource(context.Background(), server.Client(), server.URL)
	if err != nil {
		t.Fatalf("discoverSource() error = %v", err)
	}
	want := discoveredSource{Kind: "disk", URL: server.URL + diskPath}
	if got != want {
		t.Fatalf("discoverSource() = %#v, want %#v", got, want)
	}
}

func TestPrepareSourcesRefreshesManifestSnapshotOnEachCall(t *testing.T) {
	diskID := "0123456789abcdef01234567"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprintf(w, `{"readOnly":true,"diskBytes":4096,"disk":%q}`, "/exports/"+diskID+"/disk")
	}))
	defer server.Close()

	configFile := filepath.Join(t.TempDir(), "servers.json")
	stateDir := t.TempDir()
	s := settings{
		Version: 1,
		Servers: []savedServer{{ID: "0123456789abcdef", Name: "Primary", URL: server.URL}},
		Slots:   []string{"", "0123456789abcdef", "", "", "", "", "", ""},
	}
	if err := saveSettings(configFile, s); err != nil {
		t.Fatalf("save settings: %v", err)
	}
	configPath := filepath.Join(stateDir, "source.conf")
	assertConfig := func(want string) {
		t.Helper()
		got, err := os.ReadFile(configPath)
		if err != nil {
			t.Fatalf("read prepared config: %v", err)
		}
		if string(got) != want {
			t.Fatalf("prepared config = %q, want %q", got, want)
		}
	}
	wantConfig := func(id string) string {
		return fmt.Sprintf("MANAGED_SLOTS=1\nSLOT_TYPES=()\nSLOT_URLS=()\nSLOT_TYPES[1]='disk'\nSLOT_URLS[1]='%s/exports/%s/disk'\n", server.URL, id)
	}

	if err := prepareSources(context.Background(), configFile, stateDir, server.Client()); err != nil {
		t.Fatalf("first prepareSources() error = %v", err)
	}
	assertConfig(wantConfig(diskID))

	diskID = "abcdef0123456789abcdef01"
	if err := prepareSources(context.Background(), configFile, stateDir, server.Client()); err != nil {
		t.Fatalf("second prepareSources() error = %v", err)
	}
	assertConfig(wantConfig(diskID))
}

func TestDiscoverSourceRejectsLegacyWebDAVWithoutProbing(t *testing.T) {
	cases := []struct {
		name        string
		getStatus   int
		contentType string
		body        string
	}{
		{name: "plain_non_json_get", getStatus: http.StatusOK, contentType: "text/plain", body: "not a JSON manifest"},
		{name: "method_not_allowed_get", getStatus: http.StatusMethodNotAllowed, contentType: "text/plain"},
		{name: "json_directory_listing", getStatus: http.StatusOK, contentType: "application/json", body: " \n[{\"name\":\"Games\",\"directory\":true}]"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			methods := []string{}
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				methods = append(methods, r.Method)
				if r.Method == "PROPFIND" {
					w.WriteHeader(http.StatusMultiStatus)
					return
				}
				w.Header().Set("Content-Type", tc.contentType)
				w.WriteHeader(tc.getStatus)
				_, _ = w.Write([]byte(tc.body))
			}))
			defer server.Close()

			_, err := discoverSource(context.Background(), server.Client(), server.URL)
			if err == nil {
				t.Fatal("expected legacy source rejection")
			}
			if !reflect.DeepEqual(methods, []string{http.MethodGet}) {
				t.Fatalf("request methods = %#v, want GET only", methods)
			}
		})
	}
}

func TestDiscoverSourceRefreshesLegacySnapshotURL(t *testing.T) {
	const old = "/exports/0123456789abcdef01234567/disk"
	const current = "/exports/abcdef0123456789abcdef01/disk"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			t.Errorf("requested %s, want root manifest", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprintf(w, `{"readOnly":true,"diskBytes":4096,"disk":%q}`, current)
	}))
	defer server.Close()
	got, err := discoverSource(context.Background(), server.Client(), server.URL+old)
	if err != nil || got.URL != server.URL+current {
		t.Fatalf("discoverSource() = %#v, %v", got, err)
	}
}

func TestPrepareSourcesRejectsLegacyWebDAVWithoutModifyingProfiles(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`[]`))
	}))
	defer server.Close()
	s := settings{
		Version: 1,
		Servers: []savedServer{{ID: "0123456789abcdef", Name: "Legacy", URL: server.URL}},
		Slots:   []string{"0123456789abcdef", "", "", "", "", "", "", ""},
	}
	path := filepath.Join(t.TempDir(), "servers.json")
	if err := saveSettings(path, s); err != nil {
		t.Fatal(err)
	}
	before, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	err = prepareSources(context.Background(), path, t.TempDir(), server.Client())
	if err == nil || !strings.Contains(err.Error(), "WebDAV") {
		t.Fatalf("prepareSources() error = %v, want explicit WebDAV rejection", err)
	}
	after, err := os.ReadFile(path)
	if err != nil || !reflect.DeepEqual(before, after) {
		t.Fatalf("saved profiles changed: %v", err)
	}
}

func TestDiscoverSourceRejectsHTTPAndMalformedManifestResponses(t *testing.T) {
	cases := []struct {
		name        string
		status      int
		contentType string
		body        string
		wantError   string
	}{
		{name: "http_503", status: http.StatusServiceUnavailable, contentType: "text/plain", body: "unavailable", wantError: "HTTP 503"},
		{name: "malformed_json", status: http.StatusOK, contentType: "application/json", body: `{"disk":`, wantError: "invalid server manifest"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", tc.contentType)
				w.WriteHeader(tc.status)
				_, _ = w.Write([]byte(tc.body))
			}))
			defer server.Close()

			_, err := discoverSource(context.Background(), server.Client(), server.URL)
			if err == nil || !strings.Contains(err.Error(), tc.wantError) {
				t.Fatalf("discoverSource() error = %v, want explicit failure containing %q", err, tc.wantError)
			}
		})
	}
}

func TestDiscoverSourceRejectsForeignAndUnsafeDiskURLs(t *testing.T) {
	const id = "0123456789abcdef01234567"
	cases := []struct {
		name      string
		disk      string
		readOnly  bool
		diskBytes int64
	}{
		{name: "foreign_origin", disk: "http://foreign.example/exports/" + id + "/disk", readOnly: true, diskBytes: 4096},
		{name: "embedded_credentials", disk: "http://user@local.example/exports/" + id + "/disk", readOnly: true, diskBytes: 4096},
		{name: "query_string", disk: "/exports/" + id + "/disk?download=1", readOnly: true, diskBytes: 4096},
		{name: "unsafe_path", disk: "/exports/../disk", readOnly: true, diskBytes: 4096},
		{name: "writable_disk", disk: "/exports/" + id + "/disk", readOnly: false, diskBytes: 4096},
		{name: "non_positive_disk_bytes", disk: "/exports/" + id + "/disk", readOnly: true, diskBytes: 0},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var server *httptest.Server
			server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				disk := tc.disk
				if tc.name == "embedded_credentials" {
					disk = "http://user@" + strings.TrimPrefix(server.URL, "http://") + "/exports/" + id + "/disk"
				}
				_, _ = fmt.Fprintf(w, `{"readOnly":%t,"diskBytes":%d,"disk":%q}`, tc.readOnly, tc.diskBytes, disk)
			}))
			defer server.Close()

			_, err := discoverSource(context.Background(), server.Client(), server.URL)
			if err == nil || !strings.Contains(err.Error(), "invalid read-only disk manifest") {
				t.Fatalf("discoverSource() error = %v, want explicit invalid disk manifest failure", err)
			}
		})
	}
}

func TestDiscoverSourceRejectsUnsafeServerURLs(t *testing.T) {
	cases := []struct {
		name    string
		address string
	}{
		{name: "non_http_scheme", address: "file:///etc/passwd"},
		{name: "embedded_credentials", address: "https://admin:secret@example.test"},
		{name: "fragment", address: "https://example.test/#section"},
		{name: "control_character", address: "https://example.test/\nheader"},
		{name: "overlong_url", address: "https://example.test/" + strings.Repeat("a", 2048)},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			_, err := discoverSource(context.Background(), http.DefaultClient, tc.address)
			if err == nil {
				t.Fatal("discoverSource() error = nil, want unsafe server URL rejection")
			}
		})
	}
}

func TestPrepareSourcesRejectsMultipleLegacyAssignmentsWithoutChangingRuntime(t *testing.T) {
	firstID := "0123456789abcdef01234567"
	secondID := "fedcba9876543210fedcba98"
	first := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprintf(w, `{"readOnly":true,"diskBytes":2048,"disk":%q}`, "/exports/"+firstID+"/disk")
	}))
	defer first.Close()
	second := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = fmt.Fprintf(w, `{"readOnly":true,"diskBytes":4096,"disk":%q}`, "/exports/"+secondID+"/disk")
	}))
	defer second.Close()

	s := serversTestSettings(first.URL, second.URL)
	configFile := filepath.Join(t.TempDir(), "servers.json")
	if err := saveSettings(configFile, s); err != nil {
		t.Fatalf("save settings: %v", err)
	}
	stateDir := t.TempDir()
	path := filepath.Join(stateDir, "source.conf")
	if err := os.WriteFile(path, []byte("prior mapping\n"), 0600); err != nil {
		t.Fatal(err)
	}
	if err := prepareSources(context.Background(), configFile, stateDir, discoveryClient()); err == nil ||
		!strings.Contains(err.Error(), "choose one saved server") {
		t.Fatalf("multiple legacy selections were not explicitly rejected: %v", err)
	}
	got, err := os.ReadFile(filepath.Join(stateDir, "source.conf"))
	if err != nil {
		t.Fatalf("read prepared config: %v", err)
	}
	if string(got) != "prior mapping\n" {
		t.Fatalf("failed preparation overwrote runtime mapping: %q", got)
	}
}

func TestPrepareSourcesPreservesLegacyBootWhenSettingsAreAbsent(t *testing.T) {
	configFile := filepath.Join(t.TempDir(), "missing-servers.json")
	stateDir := t.TempDir()
	configPath := filepath.Join(stateDir, "source.conf")
	if err := os.WriteFile(configPath, []byte("legacy source configuration\n"), 0600); err != nil {
		t.Fatalf("write legacy source config: %v", err)
	}

	if err := prepareSources(context.Background(), configFile, stateDir, http.DefaultClient); err != nil {
		t.Fatalf("prepareSources() error = %v", err)
	}
	if _, err := os.Stat(configPath); !os.IsNotExist(err) {
		t.Fatalf("legacy source config still exists: stat error = %v", err)
	}
}

func TestPrepareSourcesDoesNotFallbackWhenSettingsAreMalformed(t *testing.T) {
	dir := t.TempDir()
	configFile := filepath.Join(dir, "servers.json")
	if err := os.WriteFile(configFile, []byte(`{"version":`), 0600); err != nil {
		t.Fatalf("write malformed settings: %v", err)
	}
	stateDir := t.TempDir()
	configPath := filepath.Join(stateDir, "source.conf")
	legacyConfig := []byte("legacy source configuration\n")
	if err := os.WriteFile(configPath, legacyConfig, 0600); err != nil {
		t.Fatalf("write legacy source config: %v", err)
	}

	err := prepareSources(context.Background(), configFile, stateDir, http.DefaultClient)
	if err == nil {
		t.Fatal("prepareSources() error = nil, want malformed settings failure")
	}
	got, readErr := os.ReadFile(configPath)
	if readErr != nil {
		t.Fatalf("read source config after malformed settings: %v", readErr)
	}
	if !reflect.DeepEqual(got, legacyConfig) {
		t.Fatalf("source config after malformed settings = %q, want unchanged %q", got, legacyConfig)
	}
}
