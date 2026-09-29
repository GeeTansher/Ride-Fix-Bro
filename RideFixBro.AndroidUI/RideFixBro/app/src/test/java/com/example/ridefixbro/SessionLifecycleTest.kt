package com.example.ridefixbro

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.ViewModelStore
import com.example.ridefixbro.auth.AuthRepository
import com.example.ridefixbro.auth.LoginRequiredException
import com.example.ridefixbro.auth.SessionNotReadyException
import com.example.ridefixbro.auth.requireAccessToken
import com.example.ridefixbro.auth.shouldInvalidateSession
import com.example.ridefixbro.model.response.ChatResponse
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.network.RideFixApiInterface
import com.example.ridefixbro.viewmodel.AuthViewModel
import com.example.ridefixbro.viewmodel.ChatViewModel
import io.github.jan.supabase.auth.status.RefreshFailureCause
import io.github.jan.supabase.auth.status.SessionStatus
import io.github.jan.supabase.auth.user.UserInfo
import io.github.jan.supabase.auth.user.UserSession
import io.mockk.coEvery
import io.mockk.coVerify
import io.mockk.every
import io.mockk.mockk
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withContext
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.IOException
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds

@OptIn(ExperimentalCoroutinesApi::class, kotlin.time.ExperimentalTime::class)
class SessionLifecycleTest {
    private val dispatcher = StandardTestDispatcher()
    private val store = ViewModelStore()
    private val statuses = MutableStateFlow<SessionStatus>(SessionStatus.Initializing)
    private val auth = mockk<AuthRepository>()
    private val api = mockk<RideFixApiInterface>()

    @Before
    fun setup() {
        Dispatchers.setMain(dispatcher)
        every { auth.sessionStatus } returns statuses
        coEvery { auth.accessToken(any()) } answers {
            requireAccessToken(statuses.value, firstArg())
        }
        coEvery { api.me(any()) } answers {
            val user = (statuses.value as SessionStatus.Authenticated).session.user!!
            UserProfileResponse(1, user.id, "${user.id}@example.test", "User")
        }
    }

    @After
    fun cleanup() {
        store.clear()
        Dispatchers.resetMain()
    }

    @Test
    fun automaticRestoreLoadsProfileAndTokenRefreshDoesNotReloadIt() = runTest(dispatcher) {
        val model = keep(AuthViewModel(auth, api))
        runCurrent()
        assertTrue(model.state.value.loading)
        statuses.value = authenticated("user-a", "token-1")
        runCurrent()
        assertEquals("user-a", model.state.value.profile?.supabaseUserId)

        statuses.value = authenticated("user-a", "token-2")
        runCurrent()
        coVerify(exactly = 1) { api.me(any()) }
        coVerify(exactly = 1) { auth.accessToken("user-a") }
    }

    @Test
    fun sdkLogoutClearsProfileAndNewAccountGetsItsOwnProfile() = runTest(dispatcher) {
        val model = keep(AuthViewModel(auth, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        statuses.value = SessionStatus.NotAuthenticated(true)
        runCurrent()
        assertNull(model.state.value.profile)
        assertFalse(model.state.value.signedIn)
        statuses.value = authenticated("user-b")
        runCurrent()
        assertEquals("user-b", model.state.value.profile?.supabaseUserId)
    }

    @Test
    fun backgroundAndRefreshFailurePreserveChatUntilAnActualLogout() = runTest(dispatcher) {
        coEvery { api.askMechanicBro(any(), any()) } returns ChatResponse("Reply")
        val login = keep(AuthViewModel(auth, api))
        val chat = keep(ChatViewModel(auth, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.sendMessage("My question")
        runCurrent()
        val history = chat.messages.value
        assertEquals(2, history.size)

        statuses.value = SessionStatus.Initializing
        runCurrent()
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertFalse(login.state.value.sessionReady)
        assertEquals(history, chat.messages.value)

        statuses.value = SessionStatus.RefreshFailure(RefreshFailureCause.NetworkError(IOException("Offline")))
        runCurrent()
        assertFalse(login.state.value.loading)
        assertTrue(login.state.value.signedIn)
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertNotNull(login.state.value.error)
        assertEquals(history, chat.messages.value)

        statuses.value = authenticated("user-a", "refreshed-token")
        runCurrent()
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertTrue(login.state.value.sessionReady)
        assertEquals(history, chat.messages.value)
        statuses.value = SessionStatus.NotAuthenticated(true)
        runCurrent()
        assertTrue(chat.messages.value.isEmpty())
    }

    @Test
    fun draftAndCameraResultSurviveBackgroundButNeverCarryToAnotherAccount() = runTest(dispatcher) {
        val login = keep(AuthViewModel(auth, api))
        val chat = keep(ChatViewModel(auth, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.updateDraftText("Photo wala question")
        statuses.value = SessionStatus.Initializing
        runCurrent()
        chat.attachPhoto("user-a", "camera-result")
        assertEquals("Photo wala question", chat.draft.value.text)
        assertEquals("camera-result", chat.draft.value.imageData)
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertFalse(login.state.value.sessionReady)
        chat.sendDraft()
        runCurrent()
        coVerify(exactly = 0) { api.askMechanicBro(any(), any()) }

        statuses.value = authenticated("user-a", "new-token")
        runCurrent()
        assertTrue(login.state.value.sessionReady)
        assertEquals("camera-result", chat.draft.value.imageData)
        statuses.value = authenticated("user-b")
        runCurrent()
        assertEquals("", chat.draft.value.text)
        assertNull(chat.draft.value.imageData)
        chat.attachPhoto("user-a", "late-camera-result")
        assertNull(chat.draft.value.imageData)
        chat.updateDraftText("User B draft")
        statuses.value = SessionStatus.NotAuthenticated(true)
        runCurrent()
        assertEquals("", chat.draft.value.text)
    }

    @Test
    fun reconnectingProfileFailureKeepsDraftAndDisablesSendingUntilVerified() = runTest(dispatcher) {
        val login = keep(AuthViewModel(auth, api))
        val chat = keep(ChatViewModel(auth, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.updateDraftText("Keep this")
        statuses.value = SessionStatus.Initializing
        runCurrent()
        coEvery { api.me(any()) } throws IOException("Offline")
        statuses.value = authenticated("user-a", "new-token")
        runCurrent()
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertFalse(login.state.value.sessionReady)
        assertFalse(login.state.value.loading)
        assertNotNull(login.state.value.error)
        assertEquals("Keep this", chat.draft.value.text)
    }

    @Test
    fun accountSwitchCancelsOldProfileAndOldChatResponse() = runTest(dispatcher) {
        val oldProfile = CompletableDeferred<UserProfileResponse>()
        val oldReply = CompletableDeferred<ChatResponse>()
        coEvery { api.me("Bearer token-a") } coAnswers { oldProfile.await() }
        coEvery { api.askMechanicBro(any(), "Bearer token-a") } coAnswers { oldReply.await() }
        val login = keep(AuthViewModel(auth, api))
        val chat = keep(ChatViewModel(auth, api))
        statuses.value = authenticated("user-a", "token-a")
        runCurrent()
        chat.sendMessage("Private message A")
        runCurrent()
        statuses.value = authenticated("user-b", "token-b")
        runCurrent()
        assertEquals("user-b", login.state.value.profile?.supabaseUserId)
        assertTrue(chat.messages.value.isEmpty())
        oldProfile.complete(UserProfileResponse(1, "user-a", "a@example.test", "User"))
        oldReply.complete(ChatResponse("Private answer A"))
        runCurrent()
        assertEquals("user-b", login.state.value.profile?.supabaseUserId)
        assertTrue(chat.messages.value.isEmpty())
    }

    @Test
    fun logoutCannotRestoreAPendingProfileWhileRemoteSignoutRuns() = runTest(dispatcher) {
        val pendingProfile = CompletableDeferred<UserProfileResponse>()
        val logout = CompletableDeferred<Unit>()
        coEvery { api.me(any()) } coAnswers { pendingProfile.await() }
        coEvery { auth.signOut() } coAnswers {
            logout.await()
            statuses.value = SessionStatus.NotAuthenticated(true)
        }
        val model = keep(AuthViewModel(auth, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        model.signOut()
        runCurrent()
        pendingProfile.complete(UserProfileResponse(1, "user-a", "a@example.test", "User"))
        runCurrent()
        assertNull(model.state.value.profile)
        logout.complete(Unit)
        runCurrent()
        assertFalse(model.state.value.loading)
        assertNull(model.state.value.profile)
    }

    @Test
    fun apiFailureCanRetryWithoutReloadingTheSdkSession() = runTest(dispatcher) {
        coEvery { api.me(any()) } throws IOException("API unavailable")
        val model = keep(AuthViewModel(auth, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        assertNotNull(model.state.value.error)
        coEvery { api.me(any()) } returns UserProfileResponse(1, "user-a", "a@example.test", "User")
        model.retry()
        runCurrent()
        assertEquals("user-a", model.state.value.profile?.supabaseUserId)
        coVerify(exactly = 2) { auth.accessToken("user-a") }
    }

    @Test
    fun tokenGuardUsesFreshSdkTokenAndNeverForwardsExpiredOrWrongAccountToken() {
        assertEquals("new-token", requireAccessToken(authenticated("user-a", "new-token"), "user-a"))
        assertThrows(LoginRequiredException::class.java) {
            requireAccessToken(authenticated("user-b"), "user-a")
        }
        assertThrows(LoginRequiredException::class.java) {
            requireAccessToken(SessionStatus.NotAuthenticated(), "user-a")
        }
        assertThrows(SessionNotReadyException::class.java) {
            requireAccessToken(SessionStatus.Initializing, "user-a")
        }
        assertThrows(SessionNotReadyException::class.java) {
            requireAccessToken(authenticated("user-a", expiresIn = -60), "user-a")
        }
        assertThrows(SessionNotReadyException::class.java) {
            requireAccessToken(SessionStatus.RefreshFailure(
                RefreshFailureCause.NetworkError(IOException("Offline"))), "user-a")
        }
    }

    @Test
    fun delayed401CannotClearANewerTokenOrAnotherAccount() {
        assertTrue(shouldInvalidateSession(authenticated("user-a", "old"), "user-a", "old"))
        assertFalse(shouldInvalidateSession(authenticated("user-a", "new"), "user-a", "old"))
        assertFalse(shouldInvalidateSession(authenticated("user-b", "old"), "user-a", "old"))
        assertFalse(shouldInvalidateSession(SessionStatus.Initializing, "user-a", "old"))
    }

    @Test
    fun cancelledBackgroundRequestCannotStopTheNextRequestsSpinner() = runTest(dispatcher) {
        val cleanup = CompletableDeferred<Unit>()
        val nextReply = CompletableDeferred<ChatResponse>()
        coEvery { api.askMechanicBro(any(), any()) } coAnswers {
            if (firstArg<com.example.ridefixbro.model.request.ChatRequest>().message == "First") {
                try {
                    awaitCancellation()
                } finally {
                    withContext(NonCancellable) { cleanup.await() }
                }
            } else {
                nextReply.await()
            }
        }
        val chat = keep(ChatViewModel(auth, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.sendMessage("First")
        runCurrent()
        statuses.value = SessionStatus.Initializing
        runCurrent()
        statuses.value = authenticated("user-a", "new-token")
        runCurrent()
        chat.sendMessage("Second")
        runCurrent()
        cleanup.complete(Unit)
        runCurrent()
        assertTrue(chat.isLoading.value)
        nextReply.complete(ChatResponse("Second answer"))
        runCurrent()
        assertFalse(chat.isLoading.value)
        assertEquals("Second answer", chat.messages.value.last().text)
    }

    private inline fun <reified T : ViewModel> keep(model: T): T {
        val provider = ViewModelProvider(store, object : ViewModelProvider.Factory {
            @Suppress("UNCHECKED_CAST")
            override fun <R : ViewModel> create(modelClass: Class<R>): R = model as R
        })
        return provider[T::class.java]
    }

    private fun authenticated(userId: String, token: String = "test-token", expiresIn: Long = 3600) =
        SessionStatus.Authenticated(UserSession(
            accessToken = token,
            refreshToken = "test-refresh-token",
            expiresIn = expiresIn,
            expiresAt = Clock.System.now() + expiresIn.seconds,
            tokenType = "bearer",
            user = UserInfo(id = userId, aud = "authenticated")
        ))
}
