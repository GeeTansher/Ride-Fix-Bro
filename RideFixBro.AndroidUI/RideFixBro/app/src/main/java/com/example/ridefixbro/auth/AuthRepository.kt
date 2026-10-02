package com.example.ridefixbro.auth

import android.content.Context
import android.util.Log
import androidx.credentials.ClearCredentialStateRequest
import androidx.credentials.CredentialManager
import androidx.credentials.CustomCredential
import androidx.credentials.GetCredentialRequest
import com.example.ridefixbro.BuildConfig
import com.google.android.libraries.identity.googleid.GetSignInWithGoogleOption
import com.google.android.libraries.identity.googleid.GoogleIdTokenCredential
import io.github.jan.supabase.auth.Auth
import io.github.jan.supabase.auth.auth
import io.github.jan.supabase.auth.providers.Google
import io.github.jan.supabase.auth.providers.builtin.IDToken
import io.github.jan.supabase.auth.status.SessionStatus
import io.github.jan.supabase.createSupabaseClient
import io.github.jan.supabase.logging.LogLevel
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emitAll
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.URI
import java.net.URISyntaxException
import java.security.MessageDigest
import java.util.UUID
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds

@OptIn(kotlin.time.ExperimentalTime::class)
class AuthRepository(context: Context) {
    private val sessionManager = SecureSessionManager(context.applicationContext)
    private val credentialManager = CredentialManager.create(context.applicationContext)

    // SDK status hi source of truth hai. Flow ke andar lazy setup se config errors UI tak pahunchte hain.
    val sessionStatus: Flow<SessionStatus> = flow {
        emitAll(supabase.auth.sessionStatus)
    }

    private val supabase by lazy {
        validateAuthConfiguration(
            BuildConfig.SUPABASE_URL, BuildConfig.SUPABASE_PUBLISHABLE_KEY, BuildConfig.GOOGLE_WEB_CLIENT_ID
        )
        createSupabaseClient(BuildConfig.SUPABASE_URL, BuildConfig.SUPABASE_PUBLISHABLE_KEY) {
            requestTimeout = 45.seconds
            // SDK debug logs session/token include kar sakte hain. App errors UI mein safely dikhayenge.
            defaultLogLevel = LogLevel.NONE
            install(Auth) {
                sessionManager = this@AuthRepository.sessionManager
                autoLoadFromStorage = true
                autoSaveToStorage = true
                alwaysAutoRefresh = true
                enableLifecycleCallbacks = true
            }
        }
    }

    suspend fun signIn(activityContext: Context) {
        // Config ko Google UI kholne se pehle validate karo.
        supabase.auth.awaitInitialization()
        val rawNonce = UUID.randomUUID().toString()
        val googleOption = GetSignInWithGoogleOption.Builder(BuildConfig.GOOGLE_WEB_CLIENT_ID)
            .setNonce(hashNonce(rawNonce))
            .build()
        val request = GetCredentialRequest.Builder().addCredentialOption(googleOption).build()
        Log.d("RideFixAuth", "Requesting Google credential.")
        val googleToken = try {
            val response = credentialManager.getCredential(activityContext, request)
            val credential = response.credential
            if (credential !is CustomCredential ||
                credential.type != GoogleIdTokenCredential.TYPE_GOOGLE_ID_TOKEN_CREDENTIAL) {
                throw LoginRequiredException("Google did not return an ID token.")
            }
            GoogleIdTokenCredential.createFrom(credential.data).idToken
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            currentCoroutineContext().ensureActive()
            // Stage/type only: never log the token, account, nonce or provider response body.
            Log.w("RideFixAuth", "Google credential failed (${error.javaClass.simpleName}).")
            throw SignInStageException(SignInStage.Google, error)
        }
        // Google ko hashed nonce diya; Supabase ko original nonce verification ke liye.
        Log.d("RideFixAuth", "Google credential received; starting Supabase exchange.")
        try {
            supabase.auth.signInWith(IDToken) {
                provider = Google
                idToken = googleToken
                nonce = rawNonce
            }
            Log.d("RideFixAuth", "Supabase exchange completed.")
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            currentCoroutineContext().ensureActive()
            Log.w("RideFixAuth", "Supabase exchange failed (${error.javaClass.simpleName}).")
            throw SignInStageException(SignInStage.Supabase, error)
        }
    }

    suspend fun accessToken(expectedUserId: String): String {
        supabase.auth.awaitInitialization()
        // Refresh SDK karega. Hum expired/wrong-account token forward nahi karenge.
        return requireAccessToken(supabase.auth.sessionStatus.value, expectedUserId)
    }

    suspend fun invalidateSession(expectedUserId: String, rejectedToken: String) {
        val status = supabase.auth.sessionStatus.value
        if (shouldInvalidateSession(status, expectedUserId, rejectedToken)) {
            // Purane request ka 401 naye refreshed token/account ko logout nahi karega.
            supabase.auth.clearSession()
        }
    }

    suspend fun signOut() {
        var remoteLogoutFailed = false
        try {
            supabase.auth.signOut()
        } catch (error: Exception) {
            currentCoroutineContext().ensureActive()
            remoteLogoutFailed = true
        } finally {
            withContext(NonCancellable) { supabase.auth.clearSession() }
        }
        try {
            credentialManager.clearCredentialState(ClearCredentialStateRequest())
        } catch (error: Exception) {
            currentCoroutineContext().ensureActive()
            remoteLogoutFailed = true
        }
        if (remoteLogoutFailed) {
            // Local session clear hai. Already-issued access tokens expiry tak valid reh sakte hain.
            throw RemoteSignOutException()
        }
    }
}

class LoginRequiredException(message: String) : Exception(message)
class SessionNotReadyException : IOException("Session refresh is pending. Check the connection and retry.")
class AuthSetupException : Exception("Set the three public auth values in local.properties.")
class RemoteSignOutException : IOException("Local logout completed, but remote cleanup could not be confirmed.")
enum class SignInStage { Google, Supabase }
class SignInStageException(val stage: SignInStage, cause: Exception) : Exception("Sign-in failed at $stage.", cause)

@OptIn(kotlin.time.ExperimentalTime::class)
internal fun requireAccessToken(status: SessionStatus, expectedUserId: String): String {
    if (status is SessionStatus.Initializing || status is SessionStatus.RefreshFailure) {
        throw SessionNotReadyException()
    }
    if (status !is SessionStatus.Authenticated || status.session.user?.id != expectedUserId ||
        expectedUserId.isBlank() || status.session.accessToken.isBlank()) {
        throw LoginRequiredException("Sign in with the expected account.")
    }
    if (status.session.expiresAt <= Clock.System.now()) {
        throw SessionNotReadyException()
    }
    return status.session.accessToken
}

internal fun shouldInvalidateSession(status: SessionStatus, userId: String, rejectedToken: String): Boolean =
    status is SessionStatus.Authenticated &&
        status.session.user?.id == userId && status.session.accessToken == rejectedToken

internal fun validateAuthConfiguration(url: String, publishableKey: String, googleClientId: String) {
    val uri = try { URI(url) } catch (_: URISyntaxException) { throw AuthSetupException() }
    if (uri.scheme != "https" || uri.host.isNullOrBlank() || !uri.userInfo.isNullOrEmpty() ||
        !uri.query.isNullOrEmpty() || !uri.fragment.isNullOrEmpty() ||
        (uri.path != "" && uri.path != "/") || !publishableKey.startsWith("sb_publishable_") ||
        !googleClientId.endsWith(".apps.googleusercontent.com")) {
        throw AuthSetupException()
    }
}

internal fun hashNonce(rawNonce: String): String =
    MessageDigest.getInstance("SHA-256").digest(rawNonce.toByteArray(Charsets.UTF_8))
        .joinToString("") { "%02x".format(it) }
