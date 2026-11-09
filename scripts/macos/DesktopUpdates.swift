import Foundation

/// Prevents a release metadata response from redirecting a trusted check to another origin.
private final class ReleaseNoRedirect: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) { completionHandler(nil) }
}

/// Bounded official release metadata, never release-body execution or automatic installation.
struct DesktopRelease {
    let version: String
    let installer: URL?
    static let repository = "PonomarevRA/ag-memmory"

    /// The pinned public GitHub API is checked only after an explicit menu action; no tokens or private paths are sent.
    static func check(current: String, architecture: String) async throws -> DesktopRelease? {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 8; configuration.timeoutIntervalForResource = 10
        let session = URLSession(configuration: configuration, delegate: ReleaseNoRedirect(), delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        request.setValue("AgMemory/\(current)", forHTTPHeaderField: "User-Agent")
        let (bytes, response) = try await session.bytes(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200, response.expectedContentLength <= 262144 else { throw URLError(.badServerResponse) }
        var data = Data()
        for try await byte in bytes {
            guard data.count < 262144 else { throw URLError(.dataLengthExceedsMaximum) }
            data.append(byte)
        }
        return try parse(data, current: current, architecture: architecture)
    }

    /// Rejects drafts, prereleases, unexpected repositories, unsafe versions and assets for another architecture.
    static func parse(_ data: Data, current: String, architecture: String) throws -> DesktopRelease? {
        guard data.count <= 262144, let row = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              row["draft"] as? Bool == false, row["prerelease"] as? Bool == false,
              let tag = row["tag_name"] as? String, let latest = version(tag), let installed = version(current),
              let assets = row["assets"] as? [[String: Any]], assets.count <= 128 else { throw URLError(.cannotParseResponse) }
        guard latest.lexicographicallyPrecedes(installed) == false, latest != installed else { return nil }
        var installer: URL?
        for asset in assets {
            guard let name = asset["name"] as? String, name.count <= 160,
                  let raw = asset["browser_download_url"] as? String, raw.count <= 1024,
                  let url = URL(string: raw), validInstaller(url, tag: tag, name: name, architecture: architecture) else { continue }
            installer = url; break
        }
        return DesktopRelease(version: latest.map(String.init).joined(separator: "."), installer: installer)
    }

    /// Accepts only canonical three-component stable versions with an optional v prefix.
    static func version(_ value: String) -> [Int]? {
        let raw = value.hasPrefix("v") ? String(value.dropFirst()) : value
        let parts = raw.split(separator: ".", omittingEmptySubsequences: false)
        guard value.count <= 32, parts.count == 3 else { return nil }
        var result = [Int]()
        for part in parts {
            guard !part.isEmpty, part.allSatisfy({ $0.isASCII && $0.isNumber }), let number = Int(part), String(number) == part else { return nil }
            result.append(number)
        }
        return result
    }

    /// Opens only the exact pinned repository asset for this build architecture.
    static func validInstaller(_ url: URL, tag: String, name: String, architecture: String) -> Bool {
        guard let parts = URLComponents(url: url, resolvingAgainstBaseURL: false), parts.scheme == "https", parts.host == "github.com",
              parts.user == nil, parts.password == nil, parts.port == nil, parts.query == nil, parts.fragment == nil else { return false }
        let path = parts.path.split(separator: "/").map(String.init)
        guard path == ["PonomarevRA", "ag-memmory", "releases", "download", tag, name] else { return false }
        let normalized = name.lowercased()
        guard architecture == "arm64" || architecture == "x64" else { return false }
        let arm = architecture == "arm64"
        return normalized.hasPrefix("agmemory") && normalized.hasSuffix(".dmg")
            && (normalized.contains("macos") || normalized.contains("osx"))
            && (arm ? normalized.contains("arm64") && !normalized.contains("x64") && !normalized.contains("x86_64") : !normalized.contains("arm64") && (normalized.contains("x64") || normalized.contains("x86_64")))
    }
}
