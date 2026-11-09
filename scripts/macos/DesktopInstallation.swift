import Foundation

/// Reads installer preferences without moving data or modifying MCP authority.
struct DesktopInstallation: Decodable {
    let schemaVersion: String
    let appId: String
    let dataDirectory: String
    let backupDirectory: String
    let applicationPath: String
    let closeToTray: Bool?
    let launchAtLogin: Bool?
    let preferredPort: Int?

    /// The stable locator is independent of the selected data and application roots.
    static var locator: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/AGDesktop/agmemory/installation.json")
    }

    /// Absence preserves existing defaults; corrupt configuration fails before a new backend/store is started.
    static func read(_ path: URL = locator) throws -> DesktopInstallation? {
        let attributes: [FileAttributeKey: Any]
        do { attributes = try FileManager.default.attributesOfItem(atPath: path.path) }
        catch let error as NSError where error.domain == NSCocoaErrorDomain && [NSFileNoSuchFileError, NSFileReadNoSuchFileError].contains(error.code) { return nil }
        // lstat metadata distinguishes a corrupt/dangling locator from genuine absence before any file read.
        guard attributes[.type] as? FileAttributeType == .typeRegular else { throw CocoaError(.fileReadInvalidFileName) }
        let file = try FileHandle(forReadingFrom: path)
        defer { try? file.close() }
        let data = try file.read(upToCount: 65537) ?? Data()
        guard data.count <= 65536 else { throw CocoaError(.fileReadTooLarge) }
        let fields = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        for flag in ["closeToTray", "launchAtLogin"] {
            if fields?[flag] is NSNull { throw CocoaError(.fileReadCorruptFile) }
        }
        let result = try JSONDecoder().decode(DesktopInstallation.self, from: data)
        try result.validate()
        return result
    }

    /// Canonical storage roots must remain separate from each other, the app, repositories and replaceable bundles.
    private func validate() throws {
        guard schemaVersion == "ag-desktop/install-v1", appId == "agmemory",
              preferredPort == nil || (1024...65535).contains(preferredPort!),
              [dataDirectory, backupDirectory, applicationPath].allSatisfy({ $0.hasPrefix("/") && !$0.contains("\0") }) else { throw CocoaError(.fileReadCorruptFile) }
        let roots = [dataDirectory, backupDirectory, applicationPath].map { URL(fileURLWithPath: $0).standardizedFileURL.resolvingSymlinksInPath().path }
        func overlaps(_ first: String, _ second: String) -> Bool { first == second || first.hasPrefix(second + "/") || second.hasPrefix(first + "/") }
        guard roots[0] != "/", roots[1] != "/", !overlaps(roots[0], roots[1]), !overlaps(roots[0], roots[2]), !overlaps(roots[1], roots[2]) else { throw CocoaError(.fileReadCorruptFile) }
        for storage in roots.prefix(2) {
            var directory: ObjCBool = false
            if FileManager.default.fileExists(atPath: storage, isDirectory: &directory), !directory.boolValue { throw CocoaError(.fileReadCorruptFile) }
            var current = URL(fileURLWithPath: storage)
            while current.path != "/" {
                if current.pathExtension == "app" || FileManager.default.fileExists(atPath: current.appendingPathComponent(".git").path) { throw CocoaError(.fileReadCorruptFile) }
                current.deleteLastPathComponent()
            }
        }
    }

    /// Explicit process overrides keep their existing precedence over installer locations.
    static func locations(_ installed: DesktopInstallation?, environment: [String: String] = ProcessInfo.processInfo.environment) -> (data: URL, backup: URL) {
        let home = FileManager.default.homeDirectoryForCurrentUser
        var configuredData = environment["AGMEMORY_DATA_DIR"]
        var configuredBackup = environment["AGMEMORY_BACKUP_DIR"]
        if configuredData?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == true { configuredData = nil }
        if configuredBackup?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == true { configuredBackup = nil }
        let data = URL(fileURLWithPath: configuredData ?? installed?.dataDirectory ?? home.appendingPathComponent("Library/Application Support/AgMemory").path)
        let backup: String
        if let override = configuredBackup { backup = override }
        else if let installed, data.resolvingSymlinksInPath().path == URL(fileURLWithPath: installed.dataDirectory).resolvingSymlinksInPath().path {
            backup = installed.backupDirectory
        } else { backup = data.deletingLastPathComponent().appendingPathComponent("AgMemory-Backups/automatic").path }
        return (data, URL(fileURLWithPath: backup))
    }
}
