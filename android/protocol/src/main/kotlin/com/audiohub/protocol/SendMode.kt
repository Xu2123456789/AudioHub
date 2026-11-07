package com.audiohub.protocol

/**
 * 这一次点"开始"之后，声音会不会加密发送 —— 以及如果没有加密，缺的是什么。
 *
 * 与 [SenderSecurity] 的分工必须分清，否则很容易被合并成一份：
 *
 * - [interpretSenderSecurity] 回答"**用户给的两条信息是否自洽**"（给了密钥却没身份 = 参数不成立）。
 * - [resolveSendMode] 回答"**电脑要求什么，我有没有条件满足**"（电脑要求加密、手机没密钥 = 缺条件）。
 *
 * 两者都会得出"这次不加密"，但原因不同，能做的事也不同：前者要用户改输入，
 * 后者要用户去电脑上取密钥、或者去发现列表里选那台电脑。
 *
 * 做成"情况枚举 + 单一判定函数"而不是在各处写 `if`，原因是这个项目已经为
 * "同一个判定写两处"付过四次代价，其中一次是界面说"明文发送"、服务认为参数错误，
 * 于是最普通的一条通路直接起不来。
 */
enum class SendModeSituation {
    /** 有身份也有密钥，会加密。 */
    Encrypted,

    /** 明文。电脑支持加密，但这次没有密钥 —— 正常用法，不是错误。 */
    PlaintextOptional,

    /** 明文。电脑明确关掉了加密，密钥这次用不上。 */
    PlaintextOnly,

    /** 电脑要求加密，但手上没有密钥。发出去会被整包丢掉，所以必须拦下。 */
    MissingKey,

    /**
     * 电脑要求加密，但不知道它是哪台电脑（没走发现、短码也反查不到）。
     * 派生密钥要用电脑身份，所以这不是"少填一个框"，而是没法加密。
     */
    MissingIdentity,

    /** 不知道对方是谁（手填地址），只能明文。 */
    UnknownHub,
}

/**
 * 一次发送的结论。
 *
 * [advertised] 是**电脑自己广播的说法**，原样保留。界面要显示"这台电脑要求加密"这句话时
 * 必须用它，不能用 [encrypt] 反推 —— 那会把"电脑要求加密但我没密钥"显示成"明文发送"，
 * 用户在界面上看到的是许可，真实结果是发出去全被拒。
 */
data class SendModePlan(
    val advertised: Int,
    val encrypt: Boolean,
    val situation: SendModeSituation,
) {
    /** 现在点"开始"能不能连上。false 表示发出去也不会响，调用方必须拦下并说明原因。 */
    val canStart: Boolean
        get() = situation != SendModeSituation.MissingKey &&
            situation != SendModeSituation.MissingIdentity
}

/**
 * 纯函数：不碰 Android API、不读文件、不认识 View，所以能直接单测，
 * 也能被将来的其他平台照着重写二十行。
 *
 * @param advertised 电脑广播的安全模式（[SecurityMode]）；null 表示还没听到它广播，也就是不知道。
 * @param hasKey 手上有没有可用密钥（手输的、记住的、扫二维码得到的都算）。
 * @param hasIdentity 知不知道对方是哪个电脑身份（走过发现、或从配对记录/二维码拿到了 hubId）。
 */
fun resolveSendMode(
    advertised: Int?,
    hasKey: Boolean,
    hasIdentity: Boolean,
): SendModePlan {
    // 电脑明确说"关闭加密"：即使手上有密钥也不封包。
    // 这是有条测试钉着的真实故障：只收明文的电脑会把密文当作读不懂的包整个丢掉
    // （见电脑端 VbanTransport 的 None 模式），于是两边都不报错、声音就是不来。
    if (advertised == SecurityMode.NONE) {
        return SendModePlan(SecurityMode.NONE, encrypt = false, SendModeSituation.PlaintextOnly)
    }

    // 没听到广播就当"加密可选"：这是过渡期的保守取值，明文仍能通，
    // 而有密钥时照样加密，不会因为"没听到"就白丢一次加密机会。
    val mode = advertised ?: SecurityMode.OPTIONAL

    if (hasKey && hasIdentity) {
        return SendModePlan(mode, encrypt = true, SendModeSituation.Encrypted)
    }

    if (advertised == SecurityMode.REQUIRED) {
        // 缺身份和缺密钥要分开说：前者得先去选电脑/扫码，后者去电脑上取密钥。
        // 合成一句"缺少密钥"会让没扫描的人一直去翻密钥。
        return SendModePlan(
            mode,
            encrypt = false,
            if (hasIdentity) SendModeSituation.MissingKey else SendModeSituation.MissingIdentity,
        )
    }

    return SendModePlan(
        mode,
        encrypt = false,
        if (hasIdentity) SendModeSituation.PlaintextOptional else SendModeSituation.UnknownHub,
    )
}

/**
 * 真正交给发送端的两样东西：电脑身份 + 共享密钥。要么两个都在，要么都不在。
 *
 * 这个类型存在的理由是一次真实故障：判定写对了、界面也照它显示了，**唯独真正的
 * 发包动作没读这个结论** —— 电脑设了「关闭加密」、手机里还存着密钥时，界面说
 * "明文发送"，实际仍在加密；电脑只收明文，于是整包丢弃，两边零报错、声音永远不来。
 *
 * 教训是："要不要加密"不能只活在一行文案里，必须有一个**能落到字节上**的出口。
 * 出口就是 [senderCredentialsFor] 的返回值，发送端只读它。
 */
class SenderCredentials(val hubIdText: String?, val psk: ByteArray?) {

    /** 这次会加密。发送端就是按这两条 extra 判的，所以它等价于"真的封包了"。 */
    val encrypts: Boolean
        get() = hubIdText != null && psk != null
}

/**
 * 把 [SendModePlan] 翻译成要塞给发送端的两条 extra。
 *
 * 只有 [SendModePlan.encrypt] 为真、且身份与密钥都齐时才交出去；任何一条不满足都返回
 * "两样都没有"，也就是明文。**不要**在别处再写一遍这个判断 —— 上一版丢掉结论，
 * 正是因为界面自己写了一份、发送处又写了一份。
 */
fun senderCredentialsFor(
    plan: SendModePlan,
    hubIdText: String?,
    psk: ByteArray?,
): SenderCredentials {
    // plan.encrypt 已经包含了"电脑关掉加密时不许封包"这条。这里只补一件它管不到的事：
    // 派生密钥要用身份和密钥两样，缺一样就等于封不了包，不能只传一半。
    val usable = plan.encrypt &&
        !hubIdText.isNullOrBlank() &&
        psk != null &&
        psk.isNotEmpty()

    return if (usable) SenderCredentials(hubIdText, psk) else SenderCredentials(null, null)
}
