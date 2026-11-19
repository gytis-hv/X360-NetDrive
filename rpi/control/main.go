package main

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"errors"
	"flag"
	"fmt"
	"html/template"
	"log"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"
)

//go:embed index.html login.html style.css
var assets embed.FS

const sourceUnit = "x360-netdrive-source.service"
const usbUnit = "x360-netdrive.service"

type commandRunner func(context.Context, string, ...string) (string, error)

type controller struct {
	tokenHash  [32]byte
	csrf       string
	loginCSRF  string
	sessionsMu sync.Mutex
	sessions   map[[32]byte]time.Time
	stateDir   string
	configFile string
	client     *http.Client
	run        commandRunner
	mu         sync.Mutex
	page       *template.Template
}

type unitState struct {
	ActiveState string
	SubState    string
	Result      string
}

type pageData struct {
	CSRF            string
	Source          unitState
	USB             unitState
	Message         string
	Error           string
	Log             string
	Servers         []savedServer
	ActiveServer    string
	AssignmentError string
	Managed         bool
}

func runCommand(ctx context.Context, name string, args ...string) (string, error) {
	output, err := exec.CommandContext(ctx, name, args...).CombinedOutput()
	if err != nil {
		return string(output), fmt.Errorf("%s failed: %w: %s", name, err, strings.TrimSpace(string(output)))
	}
	return string(output), nil
}

func newController(token, stateDir string, run commandRunner) (*controller, error) {
	if !regexp.MustCompile(`^[A-Za-z0-9_-]{32,128}$`).MatchString(token) {
		return nil, errors.New("admin token must contain 32-128 ASCII letters, digits, underscores or hyphens")
	}
	nonce := make([]byte, 64)
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	page, err := template.ParseFS(assets, "index.html", "login.html")
	if err != nil {
		return nil, err
	}
	return &controller{
		tokenHash: sha256.Sum256([]byte(token)), csrf: hex.EncodeToString(nonce[:32]),
		loginCSRF: hex.EncodeToString(nonce[32:]), sessions: make(map[[32]byte]time.Time),
		stateDir: stateDir, run: run, page: page,
		configFile: filepath.Join(stateDir, "servers.json"), client: discoveryClient(),
	}, nil
}

func (c *controller) unit(ctx context.Context, name string) (unitState, error) {
	output, err := c.run(ctx, "systemctl", "show", name,
		"--property=ActiveState,SubState,Result", "--no-pager")
	if err != nil {
		return unitState{}, err
	}
	var state unitState
	for _, line := range strings.Split(output, "\n") {
		key, value, _ := strings.Cut(line, "=")
		switch key {
		case "ActiveState":
			state.ActiveState = value
		case "SubState":
			state.SubState = value
		case "Result":
			state.Result = value
		}
	}
	if state.ActiveState == "" {
		return state, fmt.Errorf("missing service state for %s", name)
	}
	return state, nil
}

func shellQuote(value string) string {
	return "'" + strings.ReplaceAll(value, "'", "'\"'\"'") + "'"
}

func (c *controller) action(ctx context.Context, form url.Values) error {
	action := form.Get("action")
	if (action == "mount" || action == "unmount") && form.Has("slot") {
		return errors.New("USB slot selection is no longer supported; refresh the page and choose a saved server")
	}
	switch action {
	case "add", "remove", "mount", "unmount", "apply":
		s, _, err := loadSettings(c.configFile)
		if err != nil {
			return err
		}
		if action == "add" {
			for _, server := range s.Servers {
				if server.URL == form.Get("url") {
					return errors.New("that server URL is already saved")
				}
			}
			id := make([]byte, 8)
			if _, err := rand.Read(id); err != nil {
				return err
			}
			s.Servers = append(s.Servers, savedServer{
				ID: hex.EncodeToString(id), Name: strings.TrimSpace(form.Get("name")), URL: form.Get("url"),
			})
			return saveSettings(c.configFile, s)
		}
		if action == "remove" || action == "mount" {
			index := -1
			for i, server := range s.Servers {
				if server.ID == form.Get("server") {
					index = i
					break
				}
			}
			if index < 0 {
				return errors.New("saved server not found")
			}
			if action == "remove" {
				for _, id := range s.Slots {
					if id == form.Get("server") {
						return errors.New("unmount this server before deleting it")
					}
				}
				s.Servers = append(s.Servers[:index], s.Servers[index+1:]...)
				return saveSettings(c.configFile, s)
			}
		}
		if form.Get("confirm") != "yes" {
			return errors.New("confirm the Xbox has stopped using the USB drive")
		}
		if action != "apply" {
			if action == "mount" && form.Get("server") == "" {
				return errors.New("choose a saved server")
			}
			s.Slots = make([]string, 8)
			if action == "mount" {
				s.Slots[0] = form.Get("server")
			}
		}
		return c.applyMappings(ctx, s)
	case "attach":
		s, exists, err := loadSettings(c.configFile)
		if err != nil {
			return err
		}
		if exists {
			id, err := s.activeServerID()
			if err != nil {
				return err
			}
			if id == "" {
				return errors.New("mount a saved server first")
			}
		}
		state, err := c.unit(ctx, sourceUnit)
		if err != nil {
			return err
		}
		if state.ActiveState != "active" {
			return errors.New("wait for a source to be connected before attaching USB")
		}
		_, err = c.run(ctx, "systemctl", "start", "--no-block", usbUnit)
		return err
	case "detach":
		if form.Get("confirm") != "yes" {
			return errors.New("confirm the Xbox has stopped using the drive")
		}
		if _, err := c.run(ctx, "systemctl", "stop", usbUnit); err != nil {
			return err
		}
		return nil
	default:
		return errors.New("unknown control action")
	}
}

func (c *controller) applyMappings(ctx context.Context, s settings) error {
	// Verify every selected server before interrupting the currently attached disk.
	if _, err := prepareConfig(ctx, s, c.client); err != nil {
		return err
	}
	for _, unit := range []string{usbUnit, sourceUnit} {
		if _, err := c.run(ctx, "systemctl", "stop", unit); err != nil {
			return err
		}
	}
	if err := saveSettings(c.configFile, s); err != nil {
		return fmt.Errorf("USB is detached; could not persist the selected server: %w", err)
	}
	for _, id := range s.Slots {
		if id != "" {
			_, err := c.run(ctx, "systemctl", "start", "--no-block", usbUnit)
			if err != nil {
				return fmt.Errorf("server selection saved, but USB startup failed: %w", err)
			}
			return nil
		}
	}
	return nil
}

func (c *controller) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	w.Header().Set("X-Frame-Options", "DENY")
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.Header().Set("Content-Security-Policy", "default-src 'none'; style-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'")
	if r.URL.Path == "/login" {
		c.login(w, r)
		return
	}
	if r.URL.Path == "/style.css" && r.Method == http.MethodGet {
		content, err := assets.ReadFile("style.css")
		if err != nil {
			log.Printf("read stylesheet: %v", err)
			http.Error(w, "Stylesheet unavailable", http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "text/css; charset=utf-8")
		w.Write(content)
		return
	}
	if r.URL.Path != "/" && r.URL.Path != "/logout" {
		http.NotFound(w, r)
		return
	}
	if !c.authenticated(r) {
		if r.Method == http.MethodGet {
			c.renderLogin(w, r, http.StatusOK, "")
		} else {
			http.Error(w, "Session missing or expired. Refresh the page and sign in.", http.StatusUnauthorized)
		}
		return
	}
	if r.URL.Path == "/logout" {
		c.logout(w, r)
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 65*time.Second)
	defer cancel()
	data := pageData{CSRF: c.csrf}
	status := http.StatusOK
	switch r.Method {
	case http.MethodGet:
		if r.URL.Query().Get("requested") == "1" {
			data.Message = "Request accepted. Refresh status to check mounting or USB attachment."
		}
	case http.MethodPost:
		if !validForm(w, r, c.csrf) {
			return
		}
		if !c.mu.TryLock() {
			http.Error(w, "Another control action is running. Refresh and retry.", http.StatusConflict)
			return
		}
		err := c.action(ctx, r.PostForm)
		c.mu.Unlock()
		if err != nil {
			data.Error = err.Error()
			log.Printf("control action %q failed: %v", r.PostForm.Get("action"), err)
			status = http.StatusBadRequest
		} else {
			http.Redirect(w, r, "/?requested=1", http.StatusSeeOther)
			return
		}
	default:
		w.Header().Set("Allow", "GET, POST")
		http.Error(w, "Method not allowed", http.StatusMethodNotAllowed)
		return
	}
	s, exists, err := loadSettings(c.configFile)
	if err != nil {
		data.Error = err.Error()
		status = http.StatusInternalServerError
	} else {
		data.Servers, data.Managed = s.Servers, exists
		id, assignmentErr := s.activeServerID()
		if assignmentErr != nil {
			data.AssignmentError = assignmentErr.Error()
		}
		for _, server := range s.Servers {
			if server.ID == id {
				data.ActiveServer = server.Name
				break
			}
		}
	}
	data.Source, err = c.unit(ctx, sourceUnit)
	if err == nil {
		data.USB, err = c.unit(ctx, usbUnit)
	}
	if err != nil {
		data.Error = "Cannot read service status: " + err.Error()
		status = http.StatusInternalServerError
	}
	output, err := c.run(ctx, "journalctl", "--unit="+sourceUnit, "--unit="+usbUnit,
		"--lines=15", "--no-pager", "--output=cat")
	if err != nil {
		data.Log = "Cannot read service log: " + err.Error()
	} else {
		data.Log = output
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.WriteHeader(status)
	if err := c.page.Execute(w, data); err != nil {
		log.Printf("render control page: %v", err)
	}
}

func main() {
	listen := flag.String("listen", ":8080", "LAN listen address")
	tokenFile := flag.String("token-file", "/boot/firmware/x360-admin-token.txt", "admin token file")
	stateDir := flag.String("state-dir", "/run/x360-netdrive", "runtime source selection directory")
	configFile := flag.String("config-file", "/boot/firmware/x360-netdrive/servers.json", "persistent server profiles and selected server")
	prepare := flag.Bool("prepare-sources", false, "discover assigned servers for the source service; no admin token required")
	discover := flag.String("discover-server", "", "resolve a server base URL or legacy snapshot URL")
	flag.Parse()
	if *discover != "" {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		defer cancel()
		source, err := discoverSource(ctx, discoveryClient(), *discover)
		if err != nil {
			log.Fatal(err)
		}
		fmt.Println(source.URL)
		return
	}
	if *prepare {
		ctx, cancel := context.WithTimeout(context.Background(), 50*time.Second)
		defer cancel()
		if err := prepareSources(ctx, *configFile, *stateDir, discoveryClient()); err != nil {
			log.Fatal(err)
		}
		return
	}
	token, err := os.ReadFile(*tokenFile)
	if err != nil {
		log.Fatalf("read admin token (configure it on bootfs first): %v", err)
	}
	control, err := newController(strings.TrimSpace(string(token)), *stateDir, runCommand)
	if err != nil {
		log.Fatal(err)
	}
	control.configFile = *configFile
	server := &http.Server{
		Addr: *listen, Handler: control, ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout: 10 * time.Second, WriteTimeout: 75 * time.Second,
		IdleTimeout: 30 * time.Second, MaxHeaderBytes: 8192,
	}
	log.Printf("Control page listening on %s; use only on a trusted LAN or behind HTTPS.", *listen)
	log.Fatal(server.ListenAndServe())
}
