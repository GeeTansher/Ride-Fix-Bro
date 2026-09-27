package com.example.ridefixbro

import com.example.ridefixbro.auth.AuthSetupException
import com.example.ridefixbro.auth.hashNonce
import com.example.ridefixbro.auth.validateAuthConfiguration
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

class AuthConfigurationTest {
    @Test
    fun googleNonceUsesSha256InsteadOfSendingRawNonce() {
        assertEquals(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            hashNonce("abc")
        )
    }

    @Test
    fun acceptsOnlyProjectRootAndPublishableKeyConfiguration() {
        validateAuthConfiguration("https://demo.supabase.co", "sb_publishable_example", "demo.apps.googleusercontent.com")
        assertThrows(AuthSetupException::class.java) {
            validateAuthConfiguration("https://demo.supabase.co/auth/v1", "sb_publishable_example", "demo.apps.googleusercontent.com")
        }
        assertThrows(AuthSetupException::class.java) {
            validateAuthConfiguration("http://demo.supabase.co", "sb_publishable_example", "demo.apps.googleusercontent.com")
        }
        assertThrows(AuthSetupException::class.java) {
            validateAuthConfiguration("https://demo.supabase.co", "sb_secret_do-not-use", "demo.apps.googleusercontent.com")
        }
    }
}
