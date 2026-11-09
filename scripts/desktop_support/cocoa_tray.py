"""One elected ag-systems NSStatusItem across independent macOS applications."""
import os
from threading import Lock, Thread
from time import monotonic
from .tray_group import CATALOG, NAMESPACE, EventBudget, PeerGroup


class CocoaTray:
    """Own only native menu/UI state; services keep their existing shutdown and storage ownership."""
    def __init__(self, handle, *, catalog=CATALOG, namespace=NAMESPACE):
        """Install on AppKit main; retain weak targets and use no data-bearing distributed messages."""
        import AppKit
        from Foundation import NSDistributedNotificationCenter, NSNotificationCenter, NSObject, NSTimer
        self.handle, self.catalog, self.namespace = handle, catalog, namespace
        self.app = AppKit.NSApplication.sharedApplication()
        self.item = None
        self.closed = False
        self.callbacks = []
        self.presence_budget = EventBudget(32, 2.0)
        self.command_budget = EventBudget(8, 1.0)
        self.quitting = False
        self.pending = set()
        self.pending_lock = Lock()
        self.group = PeerGroup(handle.app_id, os.getpid(), self._verify, catalog=catalog)
        owner = self

        class AGSystemsTrayTarget(NSObject):
            """AppKit/distributed callbacks are serialized on the native main thread."""
            def tick_(self, sender):
                """Refresh bounded peers and hand over menu ownership without service work."""
                owner.tick()
            def presence_(self, notification):
                """Notifications carry only an allowlisted presence string, never credentials."""
                owner.receive_presence(notification.object())
            def command_(self, notification):
                """Route only three fixed native window commands to this application."""
                action = notification.name().rsplit('.', 1)[-1]
                if owner.command_budget.allow(monotonic()):
                    owner.command(action)
            def invoke_(self, sender):
                """Resolve an own menu callback; HTTP/update work remains off the GUI thread."""
                owner.callbacks[sender.tag()]()
            def didHide_(self, notification):
                """Cmd+H keeps the same Dock-free background presentation once restoration is available."""
                if owner.handle.ready and not owner.closed:
                    owner.app.setActivationPolicy_(AppKit.NSApplicationActivationPolicyAccessory)
            def hideWindow_(self, sender):
                """Make the real yellow traffic light perform the same safe hide as window close."""
                owner.hide()
            def menuNeedsUpdate_(self, menu):
                """Build the menu from live verified GUI peers when the user opens it."""
                owner.populate(menu)

        self.target = AGSystemsTrayTarget.alloc().init()
        self.local_center = NSNotificationCenter.defaultCenter()
        self.local_center.addObserver_selector_name_object_(self.target, "didHide:", AppKit.NSApplicationDidHideNotification, self.app)
        self.center = NSDistributedNotificationCenter.defaultCenter()
        self.center.addObserver_selector_name_object_(self.target, 'presence:', namespace + '.presence', None)
        for action in ('show', 'hide', 'quit', 'activity', 'storage', 'updates'):
            self.center.addObserver_selector_name_object_(self.target, 'command:', namespace + '.' + handle.app_id + '.' + action, None)
        self.button = handle.window.native.standardWindowButton_(AppKit.NSWindowMiniaturizeButton)
        self.old_button = None
        if self.button is not None:
            self.old_button = (self.button.target(), self.button.action(), self.button.isEnabled())
            self.button.setEnabled_(True)
            self.button.setTarget_(self.target)
            self.button.setAction_('hideWindow:')
            self.button.setToolTip_('Скрыть в ag-systems')
        handle.native = self
        self.tick()
        self.timer = NSTimer.scheduledTimerWithTimeInterval_target_selector_userInfo_repeats_(2.0, self.target, 'tick:', None, True)
        # Menu tracking has its own run-loop mode; keep peer readiness alive while it is open.
        from Foundation import NSRunLoop, NSRunLoopCommonModes
        NSRunLoop.mainRunLoop().addTimer_forMode_(self.timer, NSRunLoopCommonModes)
        if handle.closed:
            self.close()

    def _verify(self, app_id, pid):
        """Reject a reused PID or a process outside the fixed desktop bundle catalogue."""
        import AppKit
        app = AppKit.NSRunningApplication.runningApplicationWithProcessIdentifier_(pid)
        return app is not None and not app.isTerminated() and app.bundleIdentifier() == self.catalog[app_id][0]

    def post(self, name, value=None):
        """Sandbox-compatible native messaging contains no userInfo dictionary or service payload."""
        self.center.postNotificationName_object_userInfo_deliverImmediately_(name, value, None, True)

    def receive_presence(self, value):
        """Cap untrusted UI coordination before OS lookups; reconcile once per timer, not per message."""
        now = monotonic()
        if not self.closed and self.presence_budget.allow(now):
            self.group.receive(value, now)

    def tick(self):
        """Elect a GUI menu owner and advertise its actual registration every two seconds."""
        if self.closed:
            return
        self.reconcile()
        self.post(self.namespace + '.presence', self.group.wire(self.item is not None))

    def reconcile(self):
        """Keep an own restore icon until the elected peer has demonstrated a registered item."""
        import AppKit
        now = monotonic()
        follower = self.group.leader(now) != self.group.pid and self.group.ready_leader(now)
        if follower and self.item is not None:
            AppKit.NSStatusBar.systemStatusBar().removeStatusItem_(self.item)
            self.item = None
        if not follower and self.item is None:
            self.item = AppKit.NSStatusBar.systemStatusBar().statusItemWithLength_(AppKit.NSVariableStatusItemLength)
            image = AppKit.NSImage.imageWithSystemSymbolName_accessibilityDescription_('square.stack.3d.up', 'ag-systems')
            if image is not None:
                image.setTemplate_(True)
                self.item.button().setImage_(image)
            else:
                self.item.button().setTitle_('AG')
            self.item.button().setToolTip_('ag-systems — приложения в фоне')
            self.item.button().setAccessibilityLabel_('ag-systems')
            menu = AppKit.NSMenu.alloc().initWithTitle_('ag-systems')
            menu.setDelegate_(self.target)
            self.item.setMenu_(menu)
        self.handle.ready = not self.closed and (self.item is not None or follower)

    def populate(self, menu):
        """Use labelled native submenus for each live service; no arbitrary URLs or process commands."""
        import AppKit
        self.group.prune(monotonic())
        menu.removeAllItems()
        self.callbacks = []
        app_ids = {self.handle.app_id, *(peer.app_id for peer in self.group.peers.values())}
        for app_id in self.catalog:
            if app_id not in app_ids:
                continue
            title = self.catalog[app_id][1]
            parent = AppKit.NSMenuItem.alloc().initWithTitle_action_keyEquivalent_(title, None, '')
            submenu = AppKit.NSMenu.alloc().initWithTitle_(title)
            parent.setSubmenu_(submenu)
            menu.addItem_(parent)
            for label, action in (('Показать', 'show'), ('Скрыть в ag-systems', 'hide'), ('Завершить ' + title, 'quit')):
                self.add_action(submenu, label, lambda app_id=app_id, action=action: self.send(app_id, action))
            if app_id != 'atlas-browser':
                submenu.addItem_(AppKit.NSMenuItem.separatorItem())
                for label, action in (('Запросы агентов', 'activity'), ('Расположение данных', 'storage'), ('Проверить обновления…', 'updates')):
                    self.add_action(submenu, label, lambda app_id=app_id, action=action: self.send(app_id, action))

    def add_action(self, menu, title, callback):
        """Retain callbacks and weak native targets for this menu revision."""
        item = menu.addItemWithTitle_action_keyEquivalent_(title, 'invoke:', '')
        item.setTarget_(self.target)
        item.setTag_(len(self.callbacks))
        self.callbacks.append(callback)

    def send(self, app_id, action):
        """Own commands run immediately; remote commands are restricted to the same fixed vocabulary."""
        if app_id == self.handle.app_id:
            self.command(action)
        else:
            self.post(self.namespace + '.' + app_id + '.' + action)

    def command(self, action):
        """UI stays on AppKit; existing bounded Quit orchestration runs on its own worker."""
        if self.closed:
            return
        if action == 'show':
            self.show()
        elif action == 'hide':
            self.hide()
        elif action == 'quit' and not self.quitting:
            self.quitting = True  # Deduplicate before creating a worker, not inside the shutdown callback.
            Thread(target=self.handle.on_quit, daemon=True).start()
        elif action in ('activity', 'storage'):
            self.run_action(action, getattr(self.handle, action))
        elif action == 'updates':
            self.handle.updates()  # This method owns its bounded worker and duplicate-request lock.

    def run_action(self, action, callback):
        """Keep at most one worker per dialog/history action even during repeated native messages."""
        with self.pending_lock:
            if action in self.pending:
                return
            self.pending.add(action)
        def run():
            """Release the action slot after completion or failure; never block the AppKit thread."""
            try:
                callback()
            finally:
                with self.pending_lock:
                    self.pending.discard(action)
        Thread(target=run, daemon=True).start()

    def hide(self):
        """Hide like Cmd+H and remove Dock presence only while a live restore menu exists."""
        import AppKit
        self.reconcile()
        if not self.handle.ready or self.closed:
            return
        self.app.hide_(None)
        self.app.setActivationPolicy_(AppKit.NSApplicationActivationPolicyAccessory)

    def show(self):
        """Restore the existing native window, Dock identity and focus without restarting a backend."""
        import AppKit
        self.app.setActivationPolicy_(AppKit.NSApplicationActivationPolicyRegular)
        self.handle.window.native.deminiaturize_(None)
        self.handle.window.native.makeKeyAndOrderFront_(None)
        self.app.unhide_(None)
        self.app.activateIgnoringOtherApps_(True)

    def close(self):
        """Stop the timer/observer before releasing the item; remaining apps elect a successor."""
        import AppKit
        if self.closed:
            return
        self.closed = True
        self.handle.ready = False
        self.timer.invalidate()
        self.center.removeObserver_(self.target)
        self.local_center.removeObserver_(self.target)
        if self.item is not None:
            AppKit.NSStatusBar.systemStatusBar().removeStatusItem_(self.item)
            self.item = None
        if self.button is not None and self.old_button is not None:
            target, action, enabled = self.old_button
            self.button.setTarget_(target)
            self.button.setAction_(action)
            self.button.setEnabled_(enabled)
