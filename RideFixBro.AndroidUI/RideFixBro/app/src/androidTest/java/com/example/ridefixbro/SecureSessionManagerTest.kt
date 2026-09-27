package com.example.ridefixbro

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.example.ridefixbro.auth.SecureSessionManager
import io.github.jan.supabase.auth.user.UserSession
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File

@OptIn(kotlin.time.ExperimentalTime::class)
@RunWith(AndroidJUnit4::class)
class SecureSessionManagerTest {
    @Test
    fun corruptStoredSessionDoesNotCrashAutomaticRestore() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val manager = SecureSessionManager(context)
        try {
            File(context.noBackupFilesDir, "supabase-session").writeText("invalid-session")
            assertNull(manager.loadSession())
        } finally {
            manager.deleteSession()
        }
    }

    @Test
    fun sessionSurvivesReloadWithoutPlaintextTokensAndCanBeDeleted() = runBlocking {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val manager = SecureSessionManager(context)
        val session = UserSession(
            accessToken = "synthetic-access-token",
            refreshToken = "synthetic-refresh-token",
            expiresIn = 3600,
            tokenType = "bearer"
        )
        try {
            manager.saveSession(session)
            val storedFile = File(context.noBackupFilesDir, "supabase-session")
            assertFalse(storedFile.readText().contains(session.accessToken))
            assertFalse(storedFile.readText().contains(session.refreshToken))
            val reloaded = SecureSessionManager(context).loadSession()
            assertEquals(session.accessToken, reloaded?.accessToken)
            assertEquals(session.refreshToken, reloaded?.refreshToken)
            assertEquals(session.expiresAt, reloaded?.expiresAt)
            manager.deleteSession()
            assertNull(manager.loadSession())
        } finally {
            manager.deleteSession()
        }
    }
}
