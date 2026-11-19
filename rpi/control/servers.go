package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"
)

type savedServer struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	URL  string `json:"url"`
}

type settings struct {
	Version int           `json:"version"`
	Servers []savedServer `json:"servers"`
	Slots   []string      `json:"slots"`
}

func (s settings) activeServerID() (string, error) {
	// Retain the old settings format, but always expose its sole selection as LUN0.
	id := ""
	for _, selected := range s.Slots {
		if selected == "" {
			continue
		}
		if id != "" {
			return "", errors.New("multiple legacy USB assignments are unsupported; choose one saved server to replace them")
		}
		id = selected
	}
	return id, nil
}

var serverIDPattern = regexp.MustCompile(`^[0-9a-f]{16}$`)
var diskPathPattern = regexp.MustCompile(`^/exports/[0-9a-f]{24}/disk$`)

func validateServerURL(address string) (*url.URL, error) {
	if len(address) > 2048 || strings.ContainsAny(address, "\r\n\t\x00") {
		return nil, errors.New("source URL is too long or contains control characters")
	}
	u, err := url.Parse(address)
	if err != nil || u.Hostname() == "" || u.User != nil || u.Fragment != "" ||
		(u.Scheme != "http" && u.Scheme != "https") {
		return nil, errors.New("enter an HTTP(S) server URL without embedded credentials or a fragment")
	}
	return u, nil
}

func (s settings) validate() error {
	if s.Version != 1 || s.Servers == nil || len(s.Slots) != 8 {
		return errors.New("server settings must have version 1, a servers array and exactly eight USB slots")
	}
	ids := make(map[string]bool)
	for _, server := range s.Servers {
		if !serverIDPattern.MatchString(server.ID) || ids[server.ID] {
			return errors.New("server settings contain an invalid or duplicate server ID")
		}
		if strings.TrimSpace(server.Name) == "" || len(server.Name) > 80 {
			return errors.New("server names must contain 1-80 characters")
		}
		if _, err := validateServerURL(server.URL); err != nil {
			return fmt.Errorf("server %q: %w", server.Name, err)
		}
		ids[server.ID] = true
	}
	for _, id := range s.Slots {
		if id != "" && !ids[id] {
			return errors.New("USB slot refers to an unknown server")
		}
	}
	return nil
}

func loadSettings(path string) (settings, bool, error) {
	s := settings{Version: 1, Servers: []savedServer{}, Slots: make([]string, 8)}
	data, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return s, false, nil
	}
	if err != nil {
		return s, false, fmt.Errorf("read saved servers: %w", err)
	}
	if len(data) > 1024*1024 {
		return s, true, errors.New("saved server settings exceed 1 MiB")
	}
	s = settings{}
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&s); err != nil {
		return s, true, fmt.Errorf("invalid saved server settings: %w", err)
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		return s, true, errors.New("saved server settings contain trailing data")
	}
	return s, true, s.validate()
}

func atomicWrite(path string, data []byte) error {
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		return err
	}
	temp, err := os.CreateTemp(filepath.Dir(path), ".x360-*")
	if err != nil {
		return err
	}
	defer os.Remove(temp.Name())
	if _, err := temp.Write(data); err != nil {
		temp.Close()
		return err
	}
	if err := temp.Sync(); err != nil {
		temp.Close()
		return err
	}
	if err := temp.Close(); err != nil {
		return err
	}
	return os.Rename(temp.Name(), path)
}

func saveSettings(path string, s settings) error {
	if err := s.validate(); err != nil {
		return err
	}
	data, err := json.MarshalIndent(s, "", "  ")
	if err != nil {
		return err
	}
	if err := atomicWrite(path, append(data, '\n')); err != nil {
		return fmt.Errorf("save servers on bootfs: %w", err)
	}
	return nil
}

type discoveredSource struct {
	Kind string
	URL  string
}

func discoveryClient() *http.Client {
	return &http.Client{
		Timeout: 5 * time.Second,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 5 || req.URL.Scheme != via[0].URL.Scheme || req.URL.Host != via[0].URL.Host {
				return errors.New("server redirects must stay on the same origin (maximum five redirects)")
			}
			return nil
		},
	}
}

func discoverSource(ctx context.Context, client *http.Client, address string) (discoveredSource, error) {
	base, err := validateServerURL(address)
	if err != nil {
		return discoveredSource{}, err
	}
	if diskPathPattern.MatchString(base.Path) && base.RawQuery == "" {
		base.Path = "/"
		address = base.String()
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, address, nil)
	if err != nil {
		return discoveredSource{}, err
	}
	req.Header.Set("Accept", "application/json")
	resp, err := client.Do(req)
	if err != nil {
		return discoveredSource{}, fmt.Errorf("discover server: %w", err)
	}
	defer resp.Body.Close()
	mediaType, _, _ := mime.ParseMediaType(resp.Header.Get("Content-Type"))
	if resp.StatusCode != http.StatusOK {
		return discoveredSource{}, fmt.Errorf("server discovery returned HTTP %d; use an X360 Net Server base URL (WebDAV is no longer supported)", resp.StatusCode)
	}
	if mediaType != "application/json" {
		return discoveredSource{}, errors.New("expected an X360 Net Server manifest; WebDAV sources are no longer supported")
	}
	var manifest struct {
		Disk      string `json:"disk"`
		DiskBytes int64  `json:"diskBytes"`
		ReadOnly  bool   `json:"readOnly"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 4*1024*1024)).Decode(&manifest); err != nil {
		return discoveredSource{}, fmt.Errorf("invalid server manifest (WebDAV sources are no longer supported): %w", err)
	}
	disk, err := base.Parse(manifest.Disk)
	if manifest.Disk == "" || err != nil || disk.Scheme != base.Scheme || disk.Host != base.Host ||
		disk.User != nil || !diskPathPattern.MatchString(disk.Path) ||
		disk.RawQuery != "" || disk.Fragment != "" || !manifest.ReadOnly || manifest.DiskBytes <= 0 {
		return discoveredSource{}, errors.New("server returned an invalid read-only disk manifest; WebDAV sources are no longer supported")
	}
	return discoveredSource{Kind: "disk", URL: disk.String()}, nil
}

func prepareConfig(ctx context.Context, s settings, client *http.Client) (string, error) {
	if err := s.validate(); err != nil {
		return "", err
	}
	config := "MANAGED_SLOTS=1\nSLOT_TYPES=()\nSLOT_URLS=()\n"
	id, err := s.activeServerID()
	if err != nil {
		return "", err
	}
	for _, server := range s.Servers {
		if server.ID != id {
			continue
		}
		source, err := discoverSource(ctx, client, server.URL)
		if err != nil {
			return "", fmt.Errorf("%s: %w", server.Name, err)
		}
		config += fmt.Sprintf("SLOT_TYPES[1]=%s\nSLOT_URLS[1]=%s\n",
			shellQuote(source.Kind), shellQuote(source.URL))
	}
	return config, nil
}

func prepareSources(ctx context.Context, configFile, stateDir string, client *http.Client) error {
	s, exists, err := loadSettings(configFile)
	if err != nil {
		return err
	}
	path := filepath.Join(stateDir, "source.conf")
	if !exists {
		// With no saved profiles yet, retain the original bootfs configuration.
		if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
			return err
		}
		return nil
	}
	config, err := prepareConfig(ctx, s, client)
	if err != nil {
		return err
	}
	return atomicWrite(path, []byte(config))
}
