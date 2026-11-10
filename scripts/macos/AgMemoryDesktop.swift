import AppKit
import WebKit

/// Accepts only the ephemeral origin announced by this launcher's own bundled child.
struct DesktopOrigin: Equatable {
    let url: URL
    let port: Int

    init?(_ value: String) {
        guard let parts = URLComponents(string: value), parts.scheme == "http", parts.host == "127.0.0.1",
              let port = parts.port, (1...65535).contains(port), parts.user == nil, parts.password == nil,
              parts.query == nil, parts.fragment == nil, parts.path.isEmpty || parts.path == "/",
              let url = parts.url else { return nil }
        self.url = url
        self.port = port
    }

    func contains(_ candidate: URL) -> Bool {
        guard let parts = URLComponents(url: candidate, resolvingAgainstBaseURL: false) else { return false }
        return parts.scheme == "http" && parts.host == "127.0.0.1" && parts.port == port && parts.user == nil && parts.password == nil
    }

    static func externalLink(_ candidate: URL) -> Bool {
        guard let parts = URLComponents(url: candidate, resolvingAgainstBaseURL: false) else { return false }
        return ["http", "https"].contains(parts.scheme ?? "") && !(parts.host ?? "").isEmpty && parts.user == nil && parts.password == nil
    }
}

/// Keeps the native window and web content flush below the standard macOS title bar.
final class DesktopWindow: NSWindow, NSToolbarDelegate {
    private weak var toolbarTarget: AnyObject?
    private var settingsAction: Selector?
    private let identityIdentifier = NSToolbarItem.Identifier("agmemory.identity")
    private let settingsIdentifier = NSToolbarItem.Identifier("agmemory.settings")
    private(set) var sidebarAccessory: NSTitlebarAccessoryViewController?
    private(set) var sidebarButton: NSButton?
    private(set) var sidebarDivider: NSBox?
    private var contextualTitle: NSTextField?
    private var historyButtons: [NSButton] = []
    let webView: WKWebView

    init() {
        let configuration = WKWebViewConfiguration()
        if ProcessInfo.processInfo.environment["AGMEMORY_NATIVE_QA"] == "1" || CommandLine.arguments.contains("--self-test") { configuration.websiteDataStore = .nonPersistent() }
        // The presentation bridge accepts null notifications only; native code pulls fixed bounded fields.
        configuration.userContentController.addUserScript(WKUserScript(source: "document.addEventListener('DOMContentLoaded',()=>document.documentElement.classList.add('native-desktop'));", injectionTime: .atDocumentStart, forMainFrameOnly: true))
        webView = WKWebView(frame: .zero, configuration: configuration)
        super.init(contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        title = "AgMemory"
        minSize = NSSize(width: 760, height: 520)
        isRestorable = false
        tabbingMode = .disallowed
        collectionBehavior = [.fullScreenPrimary]
        let container = NSView()
        contentView = container
        webView.translatesAutoresizingMaskIntoConstraints = false
        container.addSubview(webView)
        NSLayoutConstraint.activate([
            webView.leadingAnchor.constraint(equalTo: container.leadingAnchor), webView.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            webView.topAnchor.constraint(equalTo: container.topAnchor), webView.bottomAnchor.constraint(equalTo: container.bottomAnchor)
        ])
        webView.allowsBackForwardNavigationGestures = true
        center()
    }

    /// Installs a public leading titlebar accessory; the system keeps ownership of all window controls.
    func installSidebarToolbar(target: AnyObject, action: Selector, settingsAction: Selector? = nil) {
        guard let image = NSImage(systemSymbolName: "sidebar.left", accessibilityDescription: "Toggle Sidebar") else { return }
        toolbarTarget = target
        self.settingsAction = settingsAction
        let toolbar = NSToolbar(identifier: "agmemory.window-header")
        toolbar.delegate = self
        toolbar.allowsUserCustomization = false
        self.toolbar = toolbar
        toolbarStyle = .unifiedCompact
        titleVisibility = .hidden
        let accessory = NSTitlebarAccessoryViewController()
        accessory.layoutAttribute = .left
        accessory.view = NSView(frame: NSRect(x: 0, y: 0, width: 40, height: 38))
        let button = NSButton(image: image, target: target, action: action)
        button.isBordered = false
        button.toolTip = "Toggle Sidebar (⌘⇧S)"
        button.setAccessibilityLabel("Toggle Sidebar")
        let divider = NSBox(frame: NSRect(x: 39, y: 0, width: 1, height: 38))
        divider.boxType = .separator
        for (index, key) in ["back","forward"].enumerated() {
            let image = NSImage(systemSymbolName: index == 0 ? "chevron.left" : "chevron.right", accessibilityDescription: key)!
            let command = NSButton(image:image,target:target,action:NSSelectorFromString("windowCommand:"))
            command.identifier = NSUserInterfaceItemIdentifier(key)
            command.isBordered = false; command.isEnabled = false
            command.toolTip = index == 0 ? "Назад" : "Вперёд"
            command.setAccessibilityLabel(command.toolTip)
            accessory.view.addSubview(command); historyButtons.append(command)
        }
        accessory.view.addSubview(button)
        accessory.view.addSubview(divider)
        sidebarAccessory = accessory
        sidebarButton = button
        sidebarDivider = divider
        addTitlebarAccessoryViewController(accessory)
    }

    /// Keeps identity next to the leading sidebar accessory and settings at the trailing edge of the native header.
    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        var identifiers: [NSToolbarItem.Identifier] = [identityIdentifier, .flexibleSpace]
        if settingsAction != nil { identifiers.append(settingsIdentifier) }
        return identifiers
    }

    /// Uses the same fixed item set; the header has no arbitrary application-supplied commands.
    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] { toolbarDefaultItemIdentifiers(toolbar) }

    /// Provides a genuine native blue identity/title and the existing safe Settings route action.
    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier identifier: NSToolbarItem.Identifier, willBeInsertedIntoToolbar flag: Bool) -> NSToolbarItem? {
        let item = NSToolbarItem(itemIdentifier: identifier)
        if identifier == identityIdentifier {
            let image = NSView(frame: NSRect(x: 0, y: 0, width: 16, height: 16))
            image.wantsLayer = true
            image.layer?.backgroundColor = NSColor.systemBlue.cgColor
            image.layer?.cornerRadius = 8
            image.setAccessibilityElement(false)
            image.widthAnchor.constraint(equalToConstant: 16).isActive = true
            image.heightAnchor.constraint(equalToConstant: 16).isActive = true
            let title = NSTextField(labelWithString: "AgMemory")
            title.font = .systemFont(ofSize: 13, weight: .semibold)
            title.lineBreakMode = .byTruncatingTail
            title.widthAnchor.constraint(lessThanOrEqualToConstant: 360).isActive = true
            contextualTitle = title
            let view = NSStackView(views: [image, title])
            view.orientation = .horizontal
            view.alignment = .centerY
            view.spacing = 8
            view.frame = NSRect(x: 0, y: 0, width: 118, height: 28)
            item.label = "AgMemory"
            item.view = view
            item.visibilityPriority = .high
            return item
        }
        guard identifier == settingsIdentifier else { return nil }
        item.label = "Settings"
        item.toolTip = "Settings… (⌘,)"
        item.image = NSImage(systemSymbolName: "gearshape", accessibilityDescription: "Settings")
        item.target = toolbarTarget
        item.action = settingsAction
        return item
    }

    /// Aligns the accessory's trailing separator to a measured web boundary in native points.
    /// A narrow collapsed rail keeps the button after the traffic lights and hides only this header separator.
    func alignSidebar(boundary: CGFloat, collapsed: Bool) -> Bool {
        guard let accessory = sidebarAccessory, let button = sidebarButton, let divider = sidebarDivider else { return false }
        let frame = accessory.view.convert(accessory.view.bounds, to: nil)
        guard frame.minX.isFinite, frame.height > 0, boundary.isFinite, boundary > 0, boundary < self.frame.width else { return false }
        let safeCollapsed = collapsed && boundary <= frame.minX + 100
        let width = safeCollapsed ? 100 : boundary - frame.minX
        guard width >= 100 else { return false }
        accessory.view.setFrameSize(NSSize(width: width, height: frame.height))
        button.frame = NSRect(x: max(2, width - 36), y: (frame.height - 28) / 2, width: 28, height: 28)
        divider.frame = NSRect(x: width - 1, y: 0, width: 1, height: frame.height)
        divider.isHidden = safeCollapsed
        for (index,command) in historyButtons.enumerated() { command.frame = NSRect(x:CGFloat(index)*32+2,y:(frame.height-28)/2,width:28,height:28) }
        return true
    }
    /// Accepts presentation only; command selectors are fixed by the application, never supplied by page data.
    func applyWindowContext(_ value: Any?) -> Bool {
        guard let context = value as? [String:Any], Set(context.keys) == ["title","commands"],
              let title = context["title"] as? String, !title.isEmpty, title.count <= 256,
              !title.unicodeScalars.contains(where: { $0.value < 32 }),
              let commands = context["commands"] as? [String:[String:Any]],
              Set(commands.keys).isSubset(of:["back","forward","settings"]) else { return false }
        for (key,command) in commands {
            guard Set(command.keys) == ["enabled","hidden","label"],
                  let enabled = command["enabled"] as? Bool, let hidden = command["hidden"] as? Bool,
                  let label = command["label"] as? String, label.count <= 80 else { return false }
            if let button = historyButtons.first(where: { $0.identifier?.rawValue == key }) {
                button.isEnabled = enabled; button.isHidden = hidden; button.toolTip = label; button.setAccessibilityLabel(label)
            }
        }
        self.title = title; contextualTitle?.stringValue = title
        contextualTitle?.setAccessibilityLabel(title)
        return contextualTitle != nil
    }
}

/// Owns a single child service and its native window; no existing port or other process is adopted.
final class DesktopDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, WKNavigationDelegate, NSMenuItemValidation, WKScriptMessageHandler {
    var window: DesktopWindow?
    private var installation: DesktopInstallation?
    private var background: DesktopBackground?
    private var updateTask: Task<Void, Never>?
    private var service: Process?
    private var output: Pipe?
    private var errors: Pipe?
    private var origin: DesktopOrigin?
    private var received = Data()
    private var quitting = false
    private var quitDeadline: TerminationDeadline?
    private var startupTimer: DispatchWorkItem?
    private var searchText = ""
    private var terminationSignals: [DispatchSourceSignal] = []
    private var sidebarGeneration = 0

    func applicationDidFinishLaunching(_ notification: Notification) {
        installTerminationSignals()
        do { installation = try DesktopInstallation.read() }
        catch { showFailure("The desktop installation settings are invalid. Repair them with the installer; existing data was not moved."); return }
        NSApp.mainMenu = makeMenu()
        let window = DesktopWindow()
        self.window = window
        window.delegate = self
        window.webView.navigationDelegate = self
        window.webView.configuration.userContentController.add(self, name:"agWindowChanged")
        window.installSidebarToolbar(target: self, action: #selector(toggleSidebar), settingsAction: #selector(settings))
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        background = DesktopBackground(target: self, open: #selector(openWindow), requests: #selector(openAgentRequests),
            updates: #selector(checkUpdates), storage: #selector(storageLocations))
        startService()
    }

    /// System/terminal termination enters the same graceful owned-child shutdown as the native Quit command.
    private func installTerminationSignals() {
        for number in [SIGINT, SIGTERM] {
            signal(number, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: number, queue: .main)
            source.setEventHandler { RunLoop.main.perform(inModes: [.common, .modalPanel]) { NSApp.terminate(nil) } }
            source.resume()
            terminationSignals.append(source)
        }
    }

    /// Spawns only the adjacent bundled executable and consumes its bounded readiness line without displaying logs.
    private func startService() {
        let child = Process()
        child.executableURL = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/AgMemory.Web")
        var environment = ProcessInfo.processInfo.environment
        environment["AGMEMORY_DESKTOP_MODE"] = "1"
        environment["AGMEMORY_NATIVE_WINDOW"] = "1"
        child.environment = environment
        let output = Pipe(), errors = Pipe()
        self.output = output; self.errors = errors
        child.standardOutput = output; child.standardError = errors
        output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil; return }
            DispatchQueue.main.async { self?.acceptOutput(data) }
        }
        errors.fileHandleForReading.readabilityHandler = { handle in
            if handle.availableData.isEmpty { handle.readabilityHandler = nil }
        }
        child.terminationHandler = { [weak self] process in
            // AppKit's deferred quit runs a nested loop; the main dispatch queue may already be handling the Quit request.
            RunLoop.main.perform(inModes: [.common, .modalPanel]) {
                guard let self else { return }
                self.startupTimer?.cancel()
                if self.quitting { self.quitDeadline?.complete() }
                else { self.showFailure("The bundled service exited (code \(process.terminationStatus)). Reopen AgMemory; if it repeats, rebuild or reinstall this application.") }
            }
        }
        service = child
        do { try child.run() }
        catch { service = nil; showFailure("The bundled service could not be launched. Rebuild or reinstall AgMemory."); return }
        let deadline = DispatchWorkItem { [weak self] in
            guard let self, self.origin == nil, !self.quitting else { return }
            self.showFailure("The local service did not become ready within 30 seconds. Quit and reopen AgMemory.")
        }
        startupTimer = deadline
        DispatchQueue.main.asyncAfter(deadline: .now() + 30, execute: deadline)
    }

    /// Keeps at most 64 KiB while waiting, accepting only an exact prefix from this child's stdout.
    private func acceptOutput(_ data: Data) {
        guard origin == nil, !quitting else { return }
        received.append(data)
        if received.count > 65536 { received.removeFirst(received.count - 65536) }
        while let newline = received.firstIndex(of: 10) {
            let line = String(decoding: received[..<newline], as: UTF8.self).trimmingCharacters(in: .newlines)
            received.removeSubrange(...newline)
            let prefix = "AGMEMORY_DESKTOP_URL="
            guard line.hasPrefix(prefix), let origin = DesktopOrigin(String(line.dropFirst(prefix.count))) else { continue }
            self.origin = origin
            background?.setActivityOrigin(origin.url)
            startupTimer?.cancel()
            window?.webView.load(URLRequest(url: origin.url))
            received.removeAll(keepingCapacity: false)
            break
        }
    }

    /// Allows only the announced origin in the webview; user-activated HTTP(S) links use the system browser.
    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        guard let url = navigationAction.request.url, let origin else { decisionHandler(.cancel); return }
        if origin.contains(url) {
            if navigationAction.targetFrame == nil { webView.load(navigationAction.request); decisionHandler(.cancel) }
            else { decisionHandler(.allow) }
            return
        }
        if navigationAction.navigationType == .linkActivated && DesktopOrigin.externalLink(url) { NSWorkspace.shared.open(url) }
        decisionHandler(.cancel)
    }

    /// Redirect responses cannot escape the announced origin even if their initial action was local.
    func webView(_ webView: WKWebView, decidePolicyFor navigationResponse: WKNavigationResponse, decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
        guard let url = navigationResponse.response.url, origin?.contains(url) == true, navigationResponse.canShowMIMEType else { decisionHandler(.cancel); return }
        decisionHandler(.allow)
    }

    /// Accept only null from the owned main frame; fixed DOM pulls and generation checks own all presentation.
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.name == "agWindowChanged", message.body is NSNull, message.frameInfo.isMainFrame,
              !quitting, let origin, let url = message.frameInfo.request.url, origin.contains(url),
              let current = window?.webView.url, origin.contains(current) else { return }
        synchronizeSidebar()
    }

    /// Dispatch only application-owned navigation commands against the same local document.
    @objc func windowCommand(_ sender: NSButton) {
        guard !quitting, let key = sender.identifier?.rawValue, ["back","forward"].contains(key),
              let view = window?.webView, let url = view.url, origin?.contains(url) == true else { return }
        let selector = "[data-ag-window-command=\"" + key + "\"]"
        guard let encoded = try? JSONSerialization.data(withJSONObject:selector,options:[.fragmentsAllowed]),
              let literal = String(data:encoded,encoding:.utf8) else { return }
        view.evaluateJavaScript("(()=>{const b=document.querySelector(" + literal + ");if(b&&!b.disabled)b.click();})()", completionHandler:nil)
    }

    /// Re-measures local navigation after the SPA installs its sidebar.
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { synchronizeSidebar() }

    /// Native resizing and existing zoom commands can change the CSS-to-window scale.
    func windowDidResize(_ notification: Notification) { synchronizeSidebar() }

    /// Coalesces asynchronous layout updates; obsolete page or toggle measurements cannot overwrite a newer one.
    private func synchronizeSidebar() {
        sidebarGeneration += 1
        let generation = sidebarGeneration
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { [weak self] in self?.measureSidebar(generation: generation, attempt: 0) }
    }

    /// Pulls only fixed bounded sidebar geometry from this instance's own page, retrying briefly during SPA startup.
    private func measureSidebar(generation: Int, attempt: Int) {
        guard generation == sidebarGeneration, !quitting, let origin, let view = window?.webView,
              let url = view.url, origin.contains(url), window?.sidebarAccessory != nil else { return }
        let script = """
        (() => {
          const sidebar = document.querySelector('#primary-navigation');
          if (!sidebar || !document.querySelector('#primary-navigation')) return null;
          document.documentElement.classList.add('native-sidebar-layout');
          const bounds = sidebar.getBoundingClientRect();
          const commands={};for(const key of ['back','forward','settings']){const b=document.querySelector('[data-ag-window-command="'+key+'"]');if(b)commands[key]={enabled:b.disabled!==true,hidden:b.hidden===true,label:(b.getAttribute('aria-label')||b.title||b.textContent||key).trim().slice(0,80)};}
          const context={title:document.documentElement.dataset.agWindowTitle||'',commands};
          return { context, width: bounds.width, left: bounds.left, viewport: window.innerWidth,
                   collapsed: document.documentElement.classList.contains('sidebar-collapsed') };
        })()
        """
        view.evaluateJavaScript(script) { [weak self, weak view] result, error in
            guard let self, let view, generation == self.sidebarGeneration, !self.quitting,
                  view.url == url, origin.contains(url) else { return }
            guard error == nil, let state = result as? [String: Any], let width = state["width"] as? Double,
                  let left = state["left"] as? Double, let viewport = state["viewport"] as? Double,
                  let collapsed = state["collapsed"] as? Bool, width.isFinite, viewport.isFinite, left.isFinite,
                  abs(left) < 1, (collapsed ? abs(width - 52) < 1 : (200...580).contains(width)), viewport >= width else {
                if attempt < 20 {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in self?.measureSidebar(generation: generation, attempt: attempt + 1) }
                } else { self.removeSidebarCapability(view) }
                return
            }
            let scale = view.bounds.width / viewport
            let boundary = view.convert(view.bounds, to: nil).minX + width * scale
            guard self.window?.alignSidebar(boundary: boundary, collapsed: collapsed) == true else {
                self.removeSidebarCapability(view)
                return
            }
            guard self.window?.toolbar?.items.first?.view != nil else { self.removeSidebarCapability(view); return }
            let hasContext = self.window?.applyWindowContext(state["context"]) == true
            let commandClass = hasContext ? "add" : "remove"
            view.evaluateJavaScript("document.documentElement.classList.add('native-toolbar-ready','native-identity');document.documentElement.classList." + commandClass + "('native-window-commands');", completionHandler:nil)
        }
    }

    /// A failed native layout keeps normal browser navigation and its existing menu button usable.
    private func removeSidebarCapability(_ view: WKWebView) {
        window?.sidebarDivider?.isHidden = true
        view.evaluateJavaScript("document.documentElement.classList.remove('native-toolbar-ready','native-identity','native-sidebar-layout','native-window-commands');", completionHandler: nil)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        let failure = error as NSError
        if failure.domain == NSURLErrorDomain && failure.code == NSURLErrorCancelled { return }
        showFailure("The local page could not load. Quit and reopen AgMemory; if it repeats, rebuild or reinstall the application.")
    }

    /// Closing hides only with an available status item and explicit/default background preference; Quit always drains the owned child.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        if DesktopBackground.shouldHide(closeToTray: installation?.closeToTray ?? true, ready: background?.ready ?? false, quitting: quitting) {
            background?.hide(); return false
        }
        NSApp.terminate(sender); return false
    }

    /// Reopening reveals the existing window and backend; it never starts a second server.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { openWindow(); return true }

    /// Defers Quit only for a bounded drain; never cancels logout or asks for interaction.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        updateTask?.cancel()
        background?.close()
        guard let service, service.isRunning else { quitting = true; return .terminateNow }
        if !quitting {
            quitting = true
            startupTimer?.cancel()
            let deadline = TerminationDeadline(seconds: 7, force: {
                if service.isRunning { kill(service.processIdentifier, SIGKILL) }
            }, finished: {
                RunLoop.main.perform(inModes: [.common, .modalPanel]) { NSApp.reply(toApplicationShouldTerminate: true) }
            })
            quitDeadline = deadline
            deadline.start()
            service.terminate()
        }
        return .terminateLater
    }

    private func showFailure(_ message: String) {
        guard !quitting else { return }
        let alert = NSAlert(); alert.messageText = "AgMemory could not start"; alert.informativeText = message
        alert.addButton(withTitle: "Quit"); alert.runModal(); NSApp.terminate(nil)
    }

    /// Uses responder-chain editing commands and native WebKit/navigation actions, never decorative menu entries.
    func makeMenu() -> NSMenu {
        let bar = NSMenu()
        func group(_ title: String) -> NSMenu {
            let item = NSMenuItem(); item.title = title
            let menu = NSMenu(title: title); item.submenu = menu; bar.addItem(item); return menu
        }
        func command(_ menu: NSMenu, _ title: String, _ action: Selector, _ key: String = "", _ modifiers: NSEvent.ModifierFlags = [.command], _ target: AnyObject? = nil) {
            let item = NSMenuItem(title: title, action: action, keyEquivalent: key); item.keyEquivalentModifierMask = modifiers; item.target = target; menu.addItem(item)
        }
        let app = group("AgMemory")
        command(app, "About AgMemory", #selector(about), "", [], self)
        command(app, "Settings…", #selector(settings), ",", [.command], self); app.addItem(.separator())
        command(app, "Hide AgMemory", #selector(NSApplication.hide(_:)), "h", [.command], NSApp)
        command(app, "Hide Others", #selector(NSApplication.hideOtherApplications(_:)), "h", [.command, .option], NSApp)
        command(app, "Show All", #selector(NSApplication.unhideAllApplications(_:)), "", [], NSApp); app.addItem(.separator())
        command(app, "Check for Updates…", #selector(checkUpdates), "", [], self)
        command(app, "Storage Locations…", #selector(storageLocations), "", [], self)
        command(app, "Quit AgMemory", #selector(NSApplication.terminate(_:)), "q", [.command], NSApp)
        let file = group("File")
        command(file, "Home", #selector(home), "1", [.command], self)
        command(file, "Open Memory", #selector(memory), "o", [.command], self)
        file.addItem(.separator())
        command(file, "Print…", #selector(printPage), "p", [.command], self)
        command(file, "Close Window", #selector(NSWindow.performClose(_:)), "w", [.command], window)
        let edit = group("Edit")
        command(edit, "Undo", Selector(("undo:")), "z"); command(edit, "Redo", Selector(("redo:")), "z", [.command, .shift]); edit.addItem(.separator())
        command(edit, "Cut", #selector(NSText.cut(_:)), "x"); command(edit, "Copy", #selector(NSText.copy(_:)), "c")
        command(edit, "Paste", #selector(NSText.paste(_:)), "v"); command(edit, "Select All", #selector(NSText.selectAll(_:)), "a"); edit.addItem(.separator())
        command(edit, "Find…", #selector(find), "f", [.command], self)
        let view = group("View")
        command(view, "Toggle Sidebar", #selector(toggleSidebar), "s", [.command, .shift], self)
        command(view, "Back", #selector(back), "[", [.command], self); command(view, "Forward", #selector(forward), "]", [.command], self)
        command(view, "Reload", #selector(reload), "r", [.command], self); view.addItem(.separator())
        command(view, "Zoom In", #selector(zoomIn), "+", [.command], self); command(view, "Zoom Out", #selector(zoomOut), "-", [.command], self)
        command(view, "Actual Size", #selector(actualSize), "0", [.command], self)
        view.addItem(.separator())
        command(view, "Enter Full Screen", #selector(NSWindow.toggleFullScreen(_:)), "f", [.command, .control], window)
        let windows = group("Window")
        command(windows, "Minimize", #selector(NSWindow.performMiniaturize(_:)), "m", [.command], window)
        command(windows, "Zoom", #selector(NSWindow.performZoom(_:)), "", [], window); windows.addItem(.separator())
        command(windows, "Bring All to Front", #selector(NSApplication.arrangeInFront(_:)), "", [], NSApp)
        NSApp.windowsMenu = windows
        let help = group("Help")
        command(help, "AgMemory Help", #selector(helpPage), "?", [.command], self)
        command(help, "Connect Agents", #selector(connect), "", [], self)
        NSApp.helpMenu = help
        return bar
    }

    func validateMenuItem(_ item: NSMenuItem) -> Bool {
        if item.action == #selector(back) { return window?.webView.canGoBack == true }
        if item.action == #selector(forward) { return window?.webView.canGoForward == true }
        if item.action == #selector(about) { return true }
        return origin != nil && !quitting
    }

    @objc private func openWindow() {
        guard !quitting else { return }
        NSApp.setActivationPolicy(.regular)
        NSApp.unhide(nil)
        window?.deminiaturize(nil)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Opens the existing mini-log with one fixed local-page action; no arbitrary page-to-native command bridge exists.
    @objc private func openAgentRequests() {
        openWindow()
        guard let origin, let view = window?.webView, let url = view.url, origin.contains(url) else { return }
        view.evaluateJavaScript("(() => { const input=document.querySelector('.agent-activity input[data-setting=chat]'); if(input && !input.checked) input.click(); const restore=document.querySelector('.agent-activity [data-action=restore]'); if(restore && !restore.hidden) restore.click(); })();", completionHandler: nil)
    }

    /// Shows local locations without changing authority or migrating a live store; the installer owns relocation validation.
    @objc private func storageLocations() {
        guard !quitting else { return }
        let locations = DesktopInstallation.locations(installation)
        let alert = NSAlert()
        alert.messageText = "AgMemory Storage Locations"
        alert.informativeText = "Data: \(locations.data.path)\n\nBackups: \(locations.backup.path)\n\nUse the installer to choose different locations. Existing stores require a separate reviewed migration."
        alert.addButton(withTitle: "Close"); alert.addButton(withTitle: "Open Data"); alert.addButton(withTitle: "Open Backups")
        let choice = alert.runModal()
        let folder = choice == .alertSecondButtonReturn ? locations.data : choice == .alertThirdButtonReturn ? locations.backup : nil
        if let folder, FileManager.default.fileExists(atPath: folder.path) { NSWorkspace.shared.open(folder) }
    }

    /// Performs bounded metadata I/O asynchronously and opens only the validated official installer after a second explicit click.
    @objc private func checkUpdates() {
        guard !quitting, updateTask == nil else { return }
        let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
        #if arch(arm64)
        let architecture = "arm64"
        #else
        let architecture = "x64"
        #endif
        updateTask = Task { [weak self] in
            let result: Result<DesktopRelease?, Error>
            do { result = .success(try await DesktopRelease.check(current: current, architecture: architecture)) }
            catch { result = .failure(error) }
            await MainActor.run {
                guard let self else { return }
                self.updateTask = nil
                guard !self.quitting else { return }
                let alert = NSAlert(); alert.messageText = "AgMemory Updates"
                var installer: URL?
                switch result {
                case .failure: alert.informativeText = "Update metadata is unavailable. Try again later."
                case .success(nil): alert.informativeText = "No newer stable release was found."
                case .success(let release?):
                    installer = release.installer
                    alert.informativeText = installer == nil ? "Version \(release.version) is available, but no installer for this architecture is published." : "Version \(release.version) is available. Open its official installer download?"
                }
                alert.addButton(withTitle: installer == nil ? "OK" : "Open Installer Download")
                if installer != nil { alert.addButton(withTitle: "Later") }
                if alert.runModal() == .alertFirstButtonReturn, let installer { NSWorkspace.shared.open(installer) }
            }
        }
    }

    private func navigate(_ path: String) {
        guard let origin, let url = URL(string: path, relativeTo: origin.url)?.absoluteURL, origin.contains(url) else { return }
        window?.webView.load(URLRequest(url: url))
    }
    /// Sends one fixed DOM event to this instance's validated local main frame; no webpage-to-native bridge is added.
    @objc private func toggleSidebar() {
        guard !quitting, let origin, let view = window?.webView, let current = view.url, origin.contains(current) else { return }
        view.evaluateJavaScript("window.dispatchEvent(new Event('agmemory:toggle-sidebar'));", completionHandler: { [weak self] _, _ in self?.synchronizeSidebar() })
    }
    @objc private func about() { NSApp.orderFrontStandardAboutPanel(nil) }
    @objc private func settings() { navigate("/settings") }
    @objc private func home() { navigate("/") }
    @objc private func memory() { navigate("/memory-reader") }
    @objc private func helpPage() { navigate("/about") }
    @objc private func connect() { navigate("/connect") }
    @objc private func back() { window?.webView.goBack() }
    @objc private func forward() { window?.webView.goForward() }
    @objc private func reload() { window?.webView.reload() }
    @objc private func zoomIn() { if let view = window?.webView { view.pageZoom = min(3, view.pageZoom + 0.1); synchronizeSidebar() } }
    @objc private func zoomOut() { if let view = window?.webView { view.pageZoom = max(0.5, view.pageZoom - 0.1); synchronizeSidebar() } }
    @objc private func actualSize() { window?.webView.pageZoom = 1; synchronizeSidebar() }
    @objc private func printPage() { window?.webView.printOperation(with: NSPrintInfo.shared).run() }
    @objc private func find() {
        let alert = NSAlert(); alert.messageText = "Find in AgMemory"
        let input = NSTextField(string: searchText); input.frame = NSRect(x: 0, y: 0, width: 280, height: 24); alert.accessoryView = input
        alert.addButton(withTitle: "Find"); alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn, !input.stringValue.isEmpty else { return }
        searchText = input.stringValue
        let configuration = WKFindConfiguration(); configuration.caseSensitive = false; configuration.wraps = true
        window?.webView.find(searchText, configuration: configuration) { result in if !result.matchFound { NSSound.beep() } }
    }
}

/// Checks one temporary native window and origin contracts without a service or persistent WebKit data.
func selfTest() {
    var checks = 0
    func check(_ condition: @autoclosure () -> Bool) { precondition(condition()); checks += 1 }
    let own = DesktopOrigin("http://127.0.0.1:12345/")!
    check(own.contains(URL(string: "http://127.0.0.1:12345/settings")!))
    for bad in ["https://127.0.0.1:12345/", "http://localhost:12345/", "http://127.0.0.1:0/", "http://user@127.0.0.1:12345/", "http://127.0.0.1:12345/?secret=x", "http://127.0.0.1:12345/path"] { check(DesktopOrigin(bad) == nil) }
    check(!own.contains(URL(string: "http://127.0.0.1:12346/")!))
    check(!own.contains(URL(string: "https://example.com/")!))
    for bad in ["file:///etc/passwd", "javascript:alert(1)", "data:text/html,hello", "https://user:password@example.com/"] { check(!DesktopOrigin.externalLink(URL(string: bad)!)) }
    check(DesktopOrigin.externalLink(URL(string: "https://example.com/help")!))
    let delegate = DesktopDelegate(); let menu = delegate.makeMenu()
    check(menu.items.map(\.title) == ["AgMemory", "File", "Edit", "View", "Window", "Help"])
    let commands = menu.items.flatMap { $0.submenu?.items ?? [] }
    check(commands.filter { $0.target === delegate }.allSatisfy { $0.action != nil && delegate.responds(to: $0.action!) })
    check(commands.filter { $0.action == #selector(NSWindow.toggleFullScreen(_:)) }.count == 1)
    let sidebarCommand = commands.first { $0.title == "Toggle Sidebar" }
    check(sidebarCommand?.keyEquivalent == "s" && sidebarCommand?.keyEquivalentModifierMask == [.command, .shift])
    let native = DesktopWindow()
    native.installSidebarToolbar(target: delegate, action: sidebarCommand!.action!, settingsAction: commands.first { $0.title == "Settings…" }!.action!)
    check(native.toolbar != nil && native.toolbarStyle == .unifiedCompact)
    check(native.sidebarButton?.toolTip == "Toggle Sidebar (⌘⇧S)")
    check(native.sidebarButton?.target === delegate)
    check(native.sidebarButton?.action == sidebarCommand?.action)
    check(native.titlebarAccessoryViewControllers.count == 1)
    check(native.sidebarAccessory?.layoutAttribute == .left)
    check(native.toolbar?.items.first?.label == "AgMemory")
    check(native.toolbar?.items.last?.label == "Settings")
    check(native.toolbar?.items.last?.target === delegate)
    check((native.toolbar?.items.first?.view as? NSStackView)?.arrangedSubviews.last is NSTextField)
    native.orderFront(nil)
    RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.1))
    for width: CGFloat in [1280, 760] {
        native.setContentSize(NSSize(width: width, height: 700))
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.05))
        check(native.alignSidebar(boundary: 220, collapsed: false))
        RunLoop.current.run(until: Date(timeIntervalSinceNow: 0.05))
        let button = native.sidebarButton!, divider = native.sidebarDivider!
        check(abs(divider.convert(divider.bounds, to: nil).minX - 219) < 1)
        check(button.convert(button.bounds, to: nil).maxX <= 220 && !divider.isHidden)
        let identity = native.toolbar!.items.first!.view!
        check(identity.convert(identity.bounds, to: nil).minX >= 220)
        check(native.alignSidebar(boundary: 52, collapsed: true))
        let zoom = native.standardWindowButton(.zoomButton)!
        check(divider.isHidden && button.convert(button.bounds, to: nil).minX > zoom.convert(zoom.bounds, to: nil).maxX)
    }
    let validContext: [String:Any] = ["title":"AGT-12 · Проверка", "commands":["back":["enabled":true,"hidden":false,"label":"Назад"],"forward":["enabled":false,"hidden":false,"label":"Вперёд"]]]
    check(native.applyWindowContext(validContext))
    check(native.title == "AGT-12 · Проверка")
    check(!native.applyWindowContext(["title":"unsafe\ntext","commands":[:]]))
    check(!native.applyWindowContext(["title":"Safe","commands":["execute":["enabled":true,"hidden":false,"label":"bad"]]]))
    native.orderOut(nil)
    native.contentView?.layoutSubtreeIfNeeded()
    check(native.webView.frame == native.contentView?.bounds)
    check(native.contentView?.constraints.count == 4)
    check(native.contentView?.constraints.allSatisfy { $0.constant == 0 } == true)
    check(native.standardWindowButton(.closeButton) != nil && native.standardWindowButton(.miniaturizeButton) != nil && native.standardWindowButton(.zoomButton) != nil)
    check(native.collectionBehavior.contains(.fullScreenPrimary))
    print("\(checks) native shell checks passed")
}

/// Starts the native owner; CLI integrations continue using the adjacent Web executable.
@main
struct AgMemoryDesktop {
    static func main() {
        let application = NSApplication.shared
        if CommandLine.arguments.contains("--self-test") { selfTest(); exit(0) }
        application.setActivationPolicy(.regular)
        let delegate = DesktopDelegate()
        application.delegate = delegate
        // NSApplication.delegate is weak; the native owner must outlive its run loop.
        withExtendedLifetime(delegate) { application.run() }
    }
}
