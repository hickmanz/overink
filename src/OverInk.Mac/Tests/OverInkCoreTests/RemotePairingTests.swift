import Foundation
import Testing
@testable import OverInkCore

private final class Clock: @unchecked Sendable {
    private let lock = NSLock()
    private var date: Date

    init(_ date: Date = Date(timeIntervalSince1970: 1_000_000)) {
        self.date = date
    }

    func now() -> Date { lock.withLock { date } }
    func advance(_ seconds: TimeInterval) { lock.withLock { date = date.addingTimeInterval(seconds) } }
}

@Suite struct RemotePairingTests {
    private func make(_ clock: Clock = Clock(), store: PairedDeviceStore = InMemoryPairedDeviceStore()) -> PairingService {
        PairingService(store: store, now: { clock.now() })
    }

    @Test func pairTokenWorksOnce() {
        let service = make()
        let token = service.createPairToken()
        #expect(service.tryConsumePairToken(token))
        #expect(!service.tryConsumePairToken(token))
    }

    @Test func pairTokenExpiresAfterTenMinutes() {
        let clock = Clock()
        let service = make(clock)
        let token = service.createPairToken()
        clock.advance(10 * 60 + 1)
        #expect(!service.tryConsumePairToken(token))

        let fresh = service.createPairToken()
        clock.advance(9 * 60)
        #expect(service.tryConsumePairToken(fresh))
    }

    @Test func newPairTokenReplacesTheOldOne() {
        let service = make()
        let first = service.createPairToken()
        let second = service.createPairToken()
        #expect(!service.tryConsumePairToken(first))
        #expect(service.tryConsumePairToken(second))
    }

    @Test func rejectsEmptyAndWrongTokens() {
        let service = make()
        _ = service.createPairToken()
        #expect(!service.tryConsumePairToken(nil))
        #expect(!service.tryConsumePairToken(""))
        #expect(!service.tryConsumePairToken("   "))
        #expect(!service.tryConsumePairToken("wrong"))
        #expect(!service.validateJoinToken(nil))
        #expect(!service.validateJoinToken("wrong"))
    }

    @Test func joinTokenValidatesAndRotates() {
        let service = make()
        let token = service.sessionJoinToken
        #expect(service.validateJoinToken(token))
        let rotated = service.rotateSessionJoinToken()
        #expect(rotated != token)
        #expect(!service.validateJoinToken(token))
        #expect(service.validateJoinToken(rotated))
    }

    @Test func deviceKeyIsStoredAsHashOnly() throws {
        let store = InMemoryPairedDeviceStore()
        let service = make(store: store)
        let issued = service.issueDeviceKey(deviceName: "  My iPad ", canUseLibrary: true)

        let saved = try #require(store.load().first)
        #expect(saved.name == "My iPad")
        #expect(saved.keyHash == PairingService.hash(issued.key))
        #expect(saved.keyHash != issued.key)
        #expect(saved.canUseLibrary)
        #expect(service.findByDeviceKey(issued.key)?.id == issued.device.id)
    }

    @Test func hashIsUppercaseHexSHA256() {
        // SHA-256("abc")
        #expect(PairingService.hash("abc") == "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")
    }

    @Test func unknownBlankAndRevokedKeysAreNotFound() {
        let service = make()
        let issued = service.issueDeviceKey(deviceName: "iPad", canUseLibrary: false)
        #expect(service.findByDeviceKey("nope") == nil)
        #expect(service.findByDeviceKey(nil) == nil)
        #expect(service.findByDeviceKey("") == nil)
        #expect(service.findByDeviceKey(issued.key)?.canUseLibrary == false)

        service.revoke(id: issued.device.id)
        #expect(service.findByDeviceKey(issued.key) == nil)
    }

    @Test func blankDeviceNameFallsBackAndLongNamesAreTrimmed() {
        let service = make()
        #expect(service.issueDeviceKey(deviceName: "   ", canUseLibrary: true).device.name == "iPad")
        let long = String(repeating: "x", count: 200)
        #expect(service.issueDeviceKey(deviceName: long, canUseLibrary: true).device.name.count == 64)
    }

    @Test func lookupRefreshesLastSeen() throws {
        let clock = Clock()
        let service = make(clock)
        let issued = service.issueDeviceKey(deviceName: "iPad", canUseLibrary: true)
        clock.advance(3600)
        let found = try #require(service.findByDeviceKey(issued.key))
        #expect(found.lastSeen == clock.now())
    }

    @Test func devicesSurviveRestartAndRevokeAllClears() {
        let store = InMemoryPairedDeviceStore()
        let first = make(store: store)
        let issued = first.issueDeviceKey(deviceName: "iPad", canUseLibrary: true)

        let second = make(store: store)
        #expect(second.findByDeviceKey(issued.key) != nil)
        #expect(second.pairedDevices.count == 1)

        second.revokeAll()
        #expect(store.load().isEmpty)
        #expect(make(store: store).findByDeviceKey(issued.key) == nil)
    }

    @Test func tokensAreUniqueUrlSafeAndShort() {
        let tokens = (0 ..< 50).map { _ in PairingService.makeToken() }
        #expect(Set(tokens).count == 50)
        for token in tokens {
            #expect(token.count == 22)
            #expect(token.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" })
        }
    }

    @Test func participantColorsFillThePaletteThenWrap() {
        #expect(ParticipantColors.pick(used: []) == "#E53935")
        #expect(ParticipantColors.pick(used: ["#e53935"]) == "#1E88E5")
        #expect(ParticipantColors.pick(used: ["#E53935", "#43A047"]) == "#1E88E5")
        let all = RemoteProtocol.participantColors
        #expect(all.contains(ParticipantColors.pick(used: all, random: { 3 })))
        #expect(ParticipantColors.pick(used: all, random: { 3 }) == all[3])
    }

    @Test func libraryTokensAreOnePerDeviceAndRevokeWithTheClient() {
        let registry = LibraryTokenRegistry()
        let first = registry.issue(deviceID: "d1", clientID: "c1")
        #expect(registry.isValid(first))

        let second = registry.issue(deviceID: "d1", clientID: "c2")
        #expect(!registry.isValid(first))
        #expect(registry.isValid(second))

        let other = registry.issue(deviceID: "d2", clientID: "c3")
        registry.revoke(clientID: "c2")
        #expect(!registry.isValid(second))
        #expect(registry.isValid(other))

        // After the device's token was revoked, a new connection gets a fresh one.
        let third = registry.issue(deviceID: "d1", clientID: "c4")
        #expect(registry.isValid(third))

        registry.revokeAll()
        #expect(!registry.isValid(other))
        #expect(!registry.isValid(third))
        #expect(!registry.isValid(nil))
        #expect(!registry.isValid(""))
    }
}
