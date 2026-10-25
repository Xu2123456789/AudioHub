package com.audiohub.protocol

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface

/**
 * 手机侧的发现客户端：广播一句"有 AudioHub 吗"，把回答上来的电脑收成一列。
 *
 * 用阻塞式 DatagramSocket 而不是协程封装，是为了让这个类在纯 JVM 单元测试里也能跑
 * （不需要 Robolectric，不需要手机）。真机上它由后台线程调用。
 *
 * 为什么"发广播"要做得比直觉复杂：
 * - 很多家用/校园路由器和 AP **不转发 255.255.255.255 这种全局广播**，却会转发
 *   "子网定向广播"（比如 192.168.x.255）。只发全局广播的后果是——电脑明明在跑、
 *   同一个 Wi-Fi，手机却一片安静地找不到。
 * - 手机可能同时有 Wi-Fi 和移动数据两块网卡。socket 不绑定网卡时，系统按"默认网络"
 *   决定从哪块网卡出去，开热点或 Wi-Fi 无外网时可能从蜂窝网发出去，永远到不了电脑。
 *   所以每块真实网卡都要绑一个 socket，各自往自己的子网广播。
 *
 * 兜底的那个"不绑定" socket 保留下来，一是为了兼容纯环回的单元测试，
 * 二是覆盖"只有默认网卡能出去"的场景。
 */
class DiscoveryClient(
    private val port: Int = DiscoveryProtocol.PORT,
    private val socketFactory: () -> DatagramSocket = { DatagramSocket(null) },
) {
    /**
     * 发一次询问并等回答。
     *
     * @param timeoutMs 等回答的时间。局域网里一秒绰绰有余；
     *   给多点余量是为了让第一次冷启动（电脑那边的服务可能还没起来）也能等到。
     * @return 回答上来的电脑，按名字排序，便于界面直接展示。
     */
    fun probe(timeoutMs: Long = 2_000L, token: Int = (Math.random() * Int.MAX_VALUE).toInt()): List<DiscoveredHub> {
        val found = LinkedHashMap<java.util.UUID, DiscoveredHub>()
        val probe = DiscoveryProtocol.buildProbe(token)
        val sockets = openSockets()

        try {
            // 每个 socket 都发一份；绑定到具体网卡的再多发一份该网卡的子网广播。
            for (socket in sockets) {
                for (target in targetsFor(socket)) {
                    runCatching {
                        socket.send(DatagramPacket(probe, probe.size, target, port))
                    }
                }
            }

            val deadline = System.currentTimeMillis() + timeoutMs
            val buffer = ByteArray(DiscoveryProtocol.MAX_DATAGRAM_BYTES)

            while (System.currentTimeMillis() < deadline) {
                var receivedAny = false
                for (socket in sockets) {
                    socket.soTimeout = 100
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        socket.receive(packet)
                        receivedAny = true
                    } catch (_: java.net.SocketTimeoutException) {
                        continue
                    } catch (_: java.net.SocketException) {
                        continue
                    }

                    val message = DiscoveryProtocol.parse(packet.data.copyOfRange(0, packet.length))
                    if (message is DiscoveryMessage.Announce && message.token == token) {
                        found[message.hubId] = DiscoveredHub(
                            hubId = message.hubId,
                            name = message.name,
                            host = packet.address.hostAddress ?: "",
                            vbanPort = message.vbanPort,
                            securityMode = message.securityMode,
                        )
                    }
                }
                if (!receivedAny && System.currentTimeMillis() >= deadline) {
                    break
                }
            }
        } finally {
            sockets.forEach { runCatching { it.close() } }
        }

        return found.values.sortedBy { it.name }
    }

    /**
     * 本机有哪些可以用的 IPv4（不含回环 127.* 和没拿到地址的 169.254.*）。
     * 供界面在"没找到电脑"时把地址摆出来，帮人一眼看出手机连的是不是预期网络。
     */
    companion object {
        fun localAddresses(): List<String> =
            enumerateIpv4().map { it.hostAddress ?: "" }.filter { it.isNotEmpty() }
    }

    // ------------------------------------------------------------ 打开发问用的 socket

    private fun openSockets(): List<DatagramSocket> {
        val sockets = mutableListOf<DatagramSocket>()

        // 兜底：不绑定网卡。先占默认端口，占不到就退临时端口——应答是单播回到
        // 发问的那个端口，所以退让不影响电脑答得回来。这条同时保住本地环回测试。
        val fallback = socketFactory()
        try {
            fallback.broadcast = true
            runCatching { fallback.bind(InetSocketAddress(port)) }
                .onFailure { fallback.bind(InetSocketAddress(0)) }
            sockets.add(fallback)
        } catch (_: Exception) {
            runCatching { fallback.close() }
        }

        // 每块真实网卡各绑一个 socket，让子网广播从正确的网卡出去。
        for (address in enumerateIpv4()) {
            val socket = socketFactory()
            try {
                socket.broadcast = true
                socket.bind(InetSocketAddress(address, 0))
                sockets.add(socket)
            } catch (_: Exception) {
                runCatching { socket.close() }
            }
        }

        return sockets
    }

    /** 这个 socket 该往哪些地址发：全局广播一定发；绑了具体网卡就再发该网卡的子网广播。 */
    private fun targetsFor(socket: DatagramSocket): List<InetAddress> {
        val targets = mutableListOf<InetAddress>()
        runCatching { targets.add(InetAddress.getByName("255.255.255.255")) }

        val local = socket.localAddress
        if (local is Inet4Address && !local.isAnyLocalAddress && !local.isLoopbackAddress) {
            broadcastFor(local)?.let { targets.add(it) }
        }
        return targets
    }

}

// ------------------------------------------------------------ 网卡枚举（顶层，供实例方法与 companion 共用）

private fun enumerateIpv4(): List<Inet4Address> {
    val out = mutableListOf<Inet4Address>()
    runCatching {
        val interfaces = NetworkInterface.getNetworkInterfaces() ?: return@runCatching
        for (nif in interfaces) {
            if (!nif.isUp) continue
            for (address in nif.inetAddresses.toList()) {
                if (address is Inet4Address &&
                    !address.isLoopbackAddress &&
                    !address.isLinkLocalAddress
                ) {
                    out.add(address)
                }
            }
        }
    }
    return out.distinct()
}

/** 某个网卡地址对应的子网广播地址；拿不到就返回 null。 */
private fun broadcastFor(address: Inet4Address): Inet4Address? {
    runCatching {
        val interfaces = NetworkInterface.getNetworkInterfaces() ?: return null
        for (nif in interfaces) {
            for (ia in nif.interfaceAddresses) {
                if (ia.address == address && ia.broadcast is Inet4Address) {
                    return ia.broadcast as Inet4Address
                }
            }
        }
    }
    return null
}

/** 一台回答上来的电脑。 */
data class DiscoveredHub(
    val hubId: java.util.UUID,
    val name: String,
    val host: String,
    val vbanPort: Int,
    val securityMode: Int,
) {
    /** 界面上给人看的一行。 */
    val label: String get() = if (name.isBlank()) host else "$name ($host)"
}
