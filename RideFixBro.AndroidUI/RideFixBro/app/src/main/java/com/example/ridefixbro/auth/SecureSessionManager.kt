package com.example.ridefixbro.auth

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import io.github.jan.supabase.auth.SessionManager
import io.github.jan.supabase.auth.user.UserSession
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecureSessionManager(context: Context) : SessionManager {
    private val file = AtomicFile(File(context.noBackupFilesDir, "supabase-session"))
    private val json = Json { ignoreUnknownKeys = true }
    private val keyAlias = "ridefix-auth-session"

    private fun encryptionKey(): SecretKey {
        val keyStore = KeyStore.getInstance("AndroidKeyStore")
        keyStore.load(null)
        val existing = keyStore.getKey(keyAlias, null)
        if (existing is SecretKey) return existing

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(
                keyAlias,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return generator.generateKey()
    }

    override suspend fun saveSession(session: UserSession) = withContext(Dispatchers.IO) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, encryptionKey())
        val text = json.encodeToString(UserSession.serializer(), session)
        val encrypted = cipher.doFinal(text.toByteArray(Charsets.UTF_8))
        val output = file.startWrite()
        try {
            // Key Android Keystore mein; file mein sirf IV + encrypted session. Backup mein nahi jayegi.
            output.write(cipher.iv)
            output.write(encrypted)
            file.finishWrite(output)
        } catch (error: Exception) {
            file.failWrite(output)
            throw error
        }
    }

    override suspend fun loadSession(): UserSession? = withContext(Dispatchers.IO) {
        if (!file.baseFile.exists()) return@withContext null
        val data = file.openRead().use { it.readBytes() }
        check(data.size >= 28) { "Stored login session is incomplete." }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, encryptionKey(), GCMParameterSpec(128, data.copyOfRange(0, 12)))
        val text = cipher.doFinal(data.copyOfRange(12, data.size)).toString(Charsets.UTF_8)
        json.decodeFromString(UserSession.serializer(), text)
    }

    override suspend fun deleteSession() = withContext(Dispatchers.IO) {
        file.delete()
        check(!file.baseFile.exists()) { "Stored login session could not be removed." }
    }
}
