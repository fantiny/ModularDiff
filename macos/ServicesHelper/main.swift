import Cocoa
import Darwin

// ModularDiff Finder 服务提供器:以 App 声明式服务(NSServices)注册右键菜单,
// macOS 26/27 上只有 App 声明的服务才会出现在右键菜单底部一级(workflow 服务只能进子菜单)。
// 服务触发时系统按需启动本进程,处理完即退出,无常驻。
final class ModularDiffServices: NSObject {
    static let appURL = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Applications/ModularDiff.app", isDirectory: true)
    static var stateURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("ModularDiff/pending-left")
    }

    private func selectedPaths(_ pboard: NSPasteboard) -> [String] {
        guard let urls = pboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL] else { return [] }
        return urls.map(\.path)
    }
    private func launch(_ arguments: [String]) {
        // 先把参数转发给已运行的 ModularDiff 实例(由它开新窗口);没有实例时才启动新进程。
        if forward(arguments) { return }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.arguments = arguments
        NSWorkspace.shared.openApplication(at: Self.appURL, configuration: configuration)
    }
    /// 通过 Unix domain socket 转发参数(协议:JSON 字符串数组,与 SingleInstance 一致)。
    private func forward(_ arguments: [String]) -> Bool {
        let path = NSHomeDirectory() + "/Library/Application Support/ModularDiff/ipc.sock"
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        var address = sockaddr_un()
        address.sun_family = sa_family_t(AF_UNIX)
        let bytes = Array(path.utf8CString.prefix(MemoryLayout.size(ofValue: address.sun_path) - 1)).map { UInt8(bitPattern: $0) }
        bytes.withUnsafeBufferPointer { source in
            withUnsafeMutableBytes(of: &address.sun_path) { destination in
                destination.copyBytes(from: source)
            }
        }
        let connected = withUnsafePointer(to: &address) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
            }
        }
        guard connected == 0, let payload = try? JSONEncoder().encode(arguments) else { return false }
        var sent = 0
        while sent < payload.count {
            let n = payload.withUnsafeBytes { pointer in
                send(fd, pointer.baseAddress!.advanced(by: sent), payload.count - sent, 0)
            }
            guard n > 0 else { return false }
            sent += n
        }
        shutdown(fd, SHUT_WR)
        return true
    }
    private func pendingLeft() -> String? {
        guard let text = try? String(contentsOf: Self.stateURL, encoding: .utf8) else { return nil }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    @objc func compare(_ pboard: NSPasteboard, userData: String?, error: AutoreleasingUnsafeMutablePointer<NSString?>?) {
        let paths = selectedPaths(pboard)
        if paths.count >= 2 {
            try? FileManager.default.removeItem(at: Self.stateURL)
            launch(["--left", paths[0], "--right", paths[1], "--auto-compare"])
        } else if paths.count == 1 {
            if let left = pendingLeft(), left != paths[0] {
                try? FileManager.default.removeItem(at: Self.stateURL)
                launch(["--left", left, "--right", paths[0], "--auto-compare"])
            } else {
                launch(["--left", paths[0]])
            }
        }
        quitSoon()
    }
    @objc func setLeft(_ pboard: NSPasteboard, userData: String?, error: AutoreleasingUnsafeMutablePointer<NSString?>?) {
        if let first = selectedPaths(pboard).first {
            try? FileManager.default.createDirectory(at: Self.stateURL.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? first.write(toFile: Self.stateURL.path, atomically: true, encoding: .utf8)
        }
        quitSoon()
    }
    private func quitSoon() { DispatchQueue.main.asyncAfter(deadline: .now() + 1) { NSApp.terminate(nil) } }
}

let app = NSApplication.shared
app.servicesProvider = ModularDiffServices()
app.run()
