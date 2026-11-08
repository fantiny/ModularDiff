import Cocoa
import FinderSync

@objc(FinderSync) final class FinderSync: FIFinderSync {
    static let appURL = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Applications/ModularDiff.app", isDirectory: true)
    static var stateURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("ModularDiff/pending-left")
    }

    var pendingLeft: String? {
        guard let text = try? String(contentsOf: Self.stateURL, encoding: .utf8) else { return nil }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    override init() {
        super.init()
        FIFinderSyncController.default().directoryURLs = [URL(fileURLWithPath: "/")]
    }

    override func menu(for menuKind: FIMenuKind) -> NSMenu {
        let selection = FIFinderSyncController.default().selectedItemURLs() ?? []
        let menu = NSMenu(title: "ModularDiff")
        let leftItem = NSMenuItem(title: "选择左侧文件夹进行比较", action: #selector(selectLeftFolder(_:)), keyEquivalent: "")
        leftItem.isEnabled = selection.count == 1
        menu.addItem(leftItem)
        if let left = pendingLeft {
            let name = (left as NSString).lastPathComponent
            let item = NSMenuItem(title: "与左侧“\(name)”比较", action: #selector(compareWithLeft(_:)), keyEquivalent: "")
            item.isEnabled = selection.count == 1
            menu.addItem(item)
        }
        let two = NSMenuItem(title: "比较所选的两个项目", action: #selector(compareTwo(_:)), keyEquivalent: "")
        two.isEnabled = selection.count == 2
        menu.addItem(two)
        menu.addItem(NSMenuItem.separator())
        menu.addItem(NSMenuItem(title: "打开 ModularDiff", action: #selector(openApp(_:)), keyEquivalent: ""))
        return menu
    }

    @objc private func selectLeftFolder(_ sender: Any?) {
        guard let url = (FIFinderSyncController.default().selectedItemURLs() ?? []).first else { return }
        try? FileManager.default.createDirectory(at: Self.stateURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? url.path.write(to: Self.stateURL, atomically: true, encoding: .utf8)
    }

    @objc private func compareWithLeft(_ sender: Any?) {
        guard let left = pendingLeft,
              let right = (FIFinderSyncController.default().selectedItemURLs() ?? []).first,
              left != right.path else { return }
        try? FileManager.default.removeItem(at: Self.stateURL)
        launch(["--left", left, "--right", right.path, "--auto-compare"])
    }

    @objc private func compareTwo(_ sender: Any?) {
        let selection = FIFinderSyncController.default().selectedItemURLs() ?? []
        guard selection.count == 2 else { return }
        launch(["--left", selection[0].path, "--right", selection[1].path, "--auto-compare"])
    }

    @objc private func openApp(_ sender: Any?) { launch([]) }

    private func launch(_ arguments: [String]) {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.arguments = arguments
        NSWorkspace.shared.openApplication(at: Self.appURL, configuration: configuration)
    }
}
