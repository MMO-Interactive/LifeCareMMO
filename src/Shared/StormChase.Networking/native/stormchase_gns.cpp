#include <steam/steamnetworkingsockets.h>
#include <steam/isteamnetworkingutils.h>
#include <cstdint>
#include <cstring>
#include <cstdio>
#include <deque>
#include <unordered_map>
#include <unordered_set>
#include <string>
#include <ctime>
#include "crypto.h"
#include "crypto_25519.h"
#include "steamnetworkingsockets_messages_certs.pb.h"

// Pinned upstream's certificate store: its parser, signature/expiry validation,
// and key IDs are used directly. This is the only internal API in the shim.
namespace SteamNetworkingSocketsLib {
bool CertStore_AddCertFromBase64(const char*, SteamNetworkingErrMsg&);
void CertStore_Reset();
uint64 CalculatePublicKeyID(const CECSigningPublicKey&);
}

#ifdef _WIN32
#define SC_EXPORT extern "C" __declspec(dllexport)
#else
#define SC_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {
constexpr int MaxPacket = 262164;
constexpr size_t MaxEvents = 2048;
struct Event { int kind; HSteamNetConnection connection; std::string reason; };
struct Host {
    HSteamListenSocket listener = k_HSteamListenSocket_Invalid;
    HSteamNetPollGroup group = k_HSteamNetPollGroup_Invalid;
    int maxConnections;
    std::unordered_set<HSteamNetConnection> connections;
    std::deque<Event> events;
    std::string expectedPeerIdentity;
};
std::unordered_map<HSteamListenSocket, Host*> listeners;
std::unordered_map<HSteamNetConnection, Host*> owners;
int hostCount = 0;
std::string activeAuthority;
void debug(ESteamNetworkingSocketsDebugOutputType, const char* message) { std::fprintf(stderr, "GNS: %s\n", message); }
void text(char* target, int capacity, const char* source) {
    if (!target || capacity <= 0) return;
    std::snprintf(target, static_cast<size_t>(capacity), "%s", source ? source : "");
}
void queue(Host* host, int kind, HSteamNetConnection connection, const char* reason = "") {
    if (host->events.size() < MaxEvents) host->events.push_back({kind, connection, reason});
    else SteamNetworkingSockets()->CloseConnection(connection, 4001, "Native event queue overflow", false);
}
void changed(SteamNetConnectionStatusChangedCallback_t* event) {
    auto found = owners.find(event->m_hConn);
    Host* host = found == owners.end() ? nullptr : found->second;
    if (!host && event->m_info.m_hListenSocket != k_HSteamListenSocket_Invalid) {
        auto listener = listeners.find(event->m_info.m_hListenSocket);
        if (listener != listeners.end()) host = listener->second;
    }
    if (!host) return;
    switch (event->m_info.m_eState) {
        case k_ESteamNetworkingConnectionState_Connecting:
            if (event->m_info.m_hListenSocket == k_HSteamListenSocket_Invalid) break;
            if (host->connections.size() >= static_cast<size_t>(host->maxConnections)) {
                SteamNetworkingSockets()->CloseConnection(event->m_hConn, 4002, "Server is full", false); break;
            }
            owners[event->m_hConn] = host; host->connections.insert(event->m_hConn);
            if (SteamNetworkingSockets()->AcceptConnection(event->m_hConn) != k_EResultOK || !SteamNetworkingSockets()->SetConnectionPollGroup(event->m_hConn, host->group)) {
                SteamNetworkingSockets()->CloseConnection(event->m_hConn, 4003, "Cannot accept connection", false);
            }
            break;
        case k_ESteamNetworkingConnectionState_Connected:
            // Reject any future accidental configuration downgrade to plaintext.
            if ((event->m_info.m_nFlags & k_nSteamNetworkConnectionInfoFlags_Unencrypted) != 0) {
                SteamNetworkingSockets()->CloseConnection(event->m_hConn, 4004, "Encryption is required", false);
                queue(host, 4, event->m_hConn, "Encryption is required");
            } else if (!host->expectedPeerIdentity.empty() &&
                ((event->m_info.m_nFlags & k_nSteamNetworkConnectionInfoFlags_Unauthenticated) != 0 ||
                !event->m_info.m_identityRemote.GetGenericString() || host->expectedPeerIdentity != event->m_info.m_identityRemote.GetGenericString())) {
                SteamNetworkingSockets()->CloseConnection(event->m_hConn, 4005, "Server certificate identity mismatch", false);
                queue(host, 4, event->m_hConn, "Server certificate identity mismatch");
            } else queue(host, 1, event->m_hConn);
            break;
        case k_ESteamNetworkingConnectionState_ClosedByPeer:
        case k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
            queue(host, 2, event->m_hConn, event->m_info.m_szEndDebug);
            host->connections.erase(event->m_hConn); owners.erase(event->m_hConn);
            SteamNetworkingSockets()->CloseConnection(event->m_hConn, 0, nullptr, false);
            break;
        default: break;
    }
}
int options(SteamNetworkingConfigValue_t* values) {
    values[0].SetPtr(k_ESteamNetworkingConfig_Callback_ConnectionStatusChanged, reinterpret_cast<void*>(changed));
    values[1].SetInt32(k_ESteamNetworkingConfig_Unencrypted, 0); // required, not merely preferred
    values[2].SetInt32(k_ESteamNetworkingConfig_SendBufferSize, 1024 * 1024);
    values[3].SetInt32(k_ESteamNetworkingConfig_TimeoutInitial, 10000);
    values[4].SetInt32(k_ESteamNetworkingConfig_TimeoutConnected, 10000);
    values[5].SetInt32(k_ESteamNetworkingConfig_MTU_PacketSize, 1200);
    return 6;
}
bool owned(Host* host, HSteamNetConnection connection) {
    auto item = owners.find(connection); return item != owners.end() && item->second == host;
}
}

// Calls are serialized by the managed adapter. The internal GNS service thread remains fully native.
SC_EXPORT Host* sc_host_create(uint16_t port, int maxConnections, const char* authorityBase64, const void* localCertificate, int localCertificateLength, const char* expectedPeerIdentity, char* error, int errorLength) {
    if (maxConnections < 1 || maxConnections > 256) { text(error, errorLength, "Invalid connection limit"); return nullptr; }
    if (hostCount == 0) {
        SteamNetworkingErrMsg detail;
        if (!GameNetworkingSockets_Init(nullptr, detail)) { text(error, errorLength, detail); return nullptr; }
        SteamNetworkingUtils()->SetDebugOutputFunction(k_ESteamNetworkingSocketsDebugOutputType_Warning, debug);
        SteamNetworkingSocketsLib::CertStore_Reset();
        activeAuthority = authorityBase64 ? authorityBase64 : "";
        if (!activeAuthority.empty() && !SteamNetworkingSocketsLib::CertStore_AddCertFromBase64(activeAuthority.c_str(), detail)) {
            text(error, errorLength, detail); GameNetworkingSockets_Kill(); activeAuthority.clear(); return nullptr;
        }
        if (localCertificateLength > 0 && !SteamNetworkingSockets()->SetCertificate(localCertificate, localCertificateLength, detail)) {
            text(error, errorLength, detail); GameNetworkingSockets_Kill(); activeAuthority.clear(); return nullptr;
        }
    } else if (activeAuthority != (authorityBase64 ? authorityBase64 : "") || localCertificateLength > 0) {
        text(error, errorLength, "A process can have only one active certificate trust domain / local identity"); return nullptr;
    }
    auto host = new Host(); host->maxConnections = maxConnections;
    host->expectedPeerIdentity = expectedPeerIdentity ? expectedPeerIdentity : "";
    host->group = SteamNetworkingSockets()->CreatePollGroup();
    if (port != 0) {
        SteamNetworkingIPAddr address; address.Clear(); address.m_port = port;
        SteamNetworkingConfigValue_t values[6]; int count = options(values);
        host->listener = SteamNetworkingSockets()->CreateListenSocketIP(address, count, values);
    }
    if (host->group == k_HSteamNetPollGroup_Invalid || (port != 0 && host->listener == k_HSteamListenSocket_Invalid)) {
        if (host->group) SteamNetworkingSockets()->DestroyPollGroup(host->group);
        delete host; if (hostCount == 0) GameNetworkingSockets_Kill();
        text(error, errorLength, "Cannot create listen socket (port may be in use)"); return nullptr;
    }
    if (host->listener) listeners[host->listener] = host;
    ++hostCount; text(error, errorLength, ""); return host;
}
SC_EXPORT void sc_host_destroy(Host* host) {
    if (!host) return;
    for (auto connection : host->connections) { owners.erase(connection); SteamNetworkingSockets()->CloseConnection(connection, 0, "Host disposed", false); }
    if (host->listener) { listeners.erase(host->listener); SteamNetworkingSockets()->CloseListenSocket(host->listener); }
    SteamNetworkingSockets()->DestroyPollGroup(host->group);
    delete host; if (--hostCount == 0) { GameNetworkingSockets_Kill(); activeAuthority.clear(); }
}
SC_EXPORT uint32_t sc_connect(Host* host, const char* endpoint) {
    if (!host || !endpoint || host->listener || host->connections.size() >= static_cast<size_t>(host->maxConnections)) return 0;
    SteamNetworkingIPAddr address; if (!address.ParseString(endpoint)) return 0;
    SteamNetworkingConfigValue_t values[6]; int count = options(values);
    auto connection = SteamNetworkingSockets()->ConnectByIPAddress(address, count, values);
    if (connection) { owners[connection] = host; host->connections.insert(connection); SteamNetworkingSockets()->SetConnectionPollGroup(connection, host->group); }
    return connection;
}
SC_EXPORT int sc_send(Host* host, uint32_t connection, const void* data, int length, int reliable) {
    if (!host || !owned(host, connection) || !data || length < 20 || length > MaxPacket) return k_EResultInvalidParam;
    if (!host->expectedPeerIdentity.empty()) {
        SteamNetConnectionInfo_t info;
        if (!SteamNetworkingSockets()->GetConnectionInfo(connection, &info) || info.m_eState != k_ESteamNetworkingConnectionState_Connected ||
            (info.m_nFlags & (k_nSteamNetworkConnectionInfoFlags_Unauthenticated | k_nSteamNetworkConnectionInfoFlags_Unencrypted)) != 0 ||
            !info.m_identityRemote.GetGenericString() || host->expectedPeerIdentity != info.m_identityRemote.GetGenericString()) return k_EResultAccessDenied;
    }
    return SteamNetworkingSockets()->SendMessageToConnection(connection, data, static_cast<uint32_t>(length), reliable ? k_nSteamNetworkingSend_ReliableNoNagle : k_nSteamNetworkingSend_UnreliableNoDelay, nullptr);
}
SC_EXPORT void sc_close(Host* host, uint32_t connection, const char* reason) {
    if (!host || !owned(host, connection)) return;
    SteamNetworkingSockets()->CloseConnection(connection, 4000, reason, false);
    host->connections.erase(connection); owners.erase(connection); queue(host, 2, connection, reason);
}
SC_EXPORT int sc_poll(Host* host, uint32_t* connection, void* payload, int capacity, int* length, char* reason, int reasonCapacity) {
    *connection = 0; *length = 0; text(reason, reasonCapacity, "");
    if (!host) return 0;
    SteamNetworkingSockets()->RunCallbacks();
    if (!host->events.empty()) {
        auto event = host->events.front(); host->events.pop_front(); *connection = event.connection; text(reason, reasonCapacity, event.reason.c_str()); return event.kind;
    }
    SteamNetworkingMessage_t* message = nullptr;
    if (SteamNetworkingSockets()->ReceiveMessagesOnPollGroup(host->group, &message, 1) != 1) return 0;
    *connection = message->m_conn;
    const bool valid = message->m_cbSize >= 20 && message->m_cbSize <= MaxPacket && message->m_cbSize <= capacity;
    if (valid) { *length = message->m_cbSize; std::memcpy(payload, message->m_pData, static_cast<size_t>(message->m_cbSize)); }
    message->Release();
    if (!valid) { text(reason, reasonCapacity, "Native packet exceeded payload bounds"); sc_close(host, *connection, "Invalid packet size"); return 4; }
    return 3;
}
SC_EXPORT int sc_status(Host* host, uint32_t connection, char* target, int capacity) {
    if (!host || !owned(host, connection)) { text(target, capacity, "Unknown connection"); return 0; }
    SteamNetConnectionInfo_t info;
    if (!SteamNetworkingSockets()->GetConnectionInfo(connection, &info)) return 0;
    const int prefix = std::snprintf(target, static_cast<size_t>(capacity), "Encrypted: %s\nPeer certificate authenticated: %s\n", (info.m_nFlags & k_nSteamNetworkConnectionInfoFlags_Unencrypted) ? "false" : "true", (info.m_nFlags & k_nSteamNetworkConnectionInfoFlags_Unauthenticated) ? "false" : "true");
    if (prefix > 0 && prefix < capacity) SteamNetworkingSockets()->GetDetailedConnectionStatus(connection, target + prefix, capacity - prefix);
    return 1;
}
SC_EXPORT void sc_simulate(float lossPercent, int lagMilliseconds) {
    SteamNetworkingUtils()->SetGlobalConfigValueFloat(k_ESteamNetworkingConfig_FakePacketLoss_Send, lossPercent);
    SteamNetworkingUtils()->SetGlobalConfigValueInt32(k_ESteamNetworkingConfig_FakePacketLag_Send, lagMilliseconds);
}

// Provision the exact same certificate format and native signing implementation
// used by upstream's certtool. The private CA key is discarded after issuing the
// leaf. Rotating this server identity creates a new public trust root over HTTPS.
SC_EXPORT int sc_generate_identity(const char* identity, void* authority, int authorityCapacity, int* authorityLength, void* certificate, int certificateCapacity, int* certificateLength) {
    if (!identity || std::strlen(identity) < 1 || std::strlen(identity) > 31 || hostCount != 0) return 0;
    CECSigningPublicKey caPublic, leafPublic;
    CECSigningPrivateKey caPrivate, leafPrivate;
    CCrypto::GenerateSigningKeyPair(&caPublic, &caPrivate);
    CCrypto::GenerateSigningKeyPair(&leafPublic, &leafPrivate);
    const uint64 caKeyId = SteamNetworkingSocketsLib::CalculatePublicKeyID(caPublic);
    auto make = [&](const CECSigningPublicKey& key, const char* subject, int days) {
        CMsgSteamDatagramCertificate cert;
        cert.set_key_type(CMsgSteamDatagramCertificate_EKeyType_ED25519);
        key.GetRawDataAsStdString(cert.mutable_key_data());
        cert.set_time_created(static_cast<uint32_t>(std::time(nullptr)));
        cert.set_time_expiry(cert.time_created() + static_cast<uint32_t>(days) * 86400);
        if (subject) { cert.set_identity_string(std::string("str:") + subject); cert.add_app_ids(0); }
        CMsgSteamDatagramCertificateSigned signedCert;
        signedCert.set_cert(cert.SerializeAsString());
        signedCert.set_ca_key_id(caKeyId);
        CryptoSignature_t signature;
        caPrivate.GenerateSignature(signedCert.cert().data(), signedCert.cert().size(), &signature);
        signedCert.set_ca_signature(&signature, sizeof(signature));
        return signedCert;
    };
    auto root = make(caPublic, nullptr, 3650);
    auto leaf = make(leafPublic, identity, 365);
    leafPrivate.GetRawDataAsStdString(leaf.mutable_private_key_data());
    const auto rootBytes = root.SerializeAsString(); const auto leafBytes = leaf.SerializeAsString();
    *authorityLength = static_cast<int>(rootBytes.size()); *certificateLength = static_cast<int>(leafBytes.size());
    if (*authorityLength > authorityCapacity || *certificateLength > certificateCapacity) return 0;
    std::memcpy(authority, rootBytes.data(), rootBytes.size()); std::memcpy(certificate, leafBytes.data(), leafBytes.size()); return 1;
}
