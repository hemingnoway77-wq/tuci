import Foundation
import Security

enum DeviceSecrets {
    private static var service: String { (Bundle.main.bundleIdentifier ?? "Tuci") + ".device" }
    private static func query(_ key: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service, kSecAttrAccount as String: key]
    }
    static func get(_ key: String) throws -> Data? {
        var query = query(key)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var value: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &value)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = value as? Data else { throw error(status) }
        return data
    }
    static func set(_ key: String, value: Data) throws {
        let lookup = query(key)
        let update = [kSecValueData as String: value]
        var status = SecItemUpdate(lookup as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var attributes = lookup.merging(update) { _, b in b }
            attributes[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            status = SecItemAdd(attributes as CFDictionary, nil)
        }
        if status != errSecSuccess { throw error(status) }
    }
    private static func error(_ status: OSStatus) -> NSError {
        NSError(domain: NSOSStatusErrorDomain, code: Int(status), userInfo: [NSLocalizedDescriptionKey: "设备钥匙串暂不可用（\(status)）。"])
    }
}
