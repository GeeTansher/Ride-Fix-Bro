package com.example.ridefixbro

import com.example.ridefixbro.network.RideFixBroClient
import org.junit.Assert.*
import org.junit.Test

class NetworkTimeoutTest {
    @Test
    fun chatGetsTwoMinutesWithoutExtendingLoginAndGarageCalls() {
        assertEquals(45_000, RideFixBroClient.apiClient.callTimeoutMillis)
        assertEquals(45_000, RideFixBroClient.apiClient.readTimeoutMillis)
        assertEquals(120_000, RideFixBroClient.chatClient.callTimeoutMillis)
        assertEquals(120_000, RideFixBroClient.chatClient.readTimeoutMillis)
        assertEquals(45_000, RideFixBroClient.chatClient.connectTimeoutMillis)
        assertEquals(45_000, RideFixBroClient.chatClient.writeTimeoutMillis)
        assertSame(RideFixBroClient.apiClient.connectionPool, RideFixBroClient.chatClient.connectionPool)
        assertFalse(RideFixBroClient.chatClient.followRedirects)
        assertFalse(RideFixBroClient.chatClient.retryOnConnectionFailure)
    }
}
