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
import com.example.ridefixbro.viewmodel.GarageViewModel
import com.example.ridefixbro.model.GarageBike
import com.example.ridefixbro.model.CatalogBike
import com.example.ridefixbro.model.AddGarageBikeRequest
import com.example.ridefixbro.model.ChatSummary
import com.example.ridefixbro.model.ChatDetail
import com.example.ridefixbro.model.request.ChatRequest
import com.example.ridefixbro.model.SavedChatMessage
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
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import retrofit2.HttpException
import retrofit2.Response
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds

@OptIn(ExperimentalCoroutinesApi::class, kotlin.time.ExperimentalTime::class)
class SessionLifecycleTest {
    private val dispatcher = StandardTestDispatcher()
    private val store = ViewModelStore()
    private val statuses = MutableStateFlow<SessionStatus>(SessionStatus.Initializing)
    private val auth = mockk<AuthRepository>()
    private val api = mockk<RideFixApiInterface>()
    private val savedChats = linkedMapOf<Int, ChatSummary>()

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
        coEvery { api.askMechanicBro(any(), any()) } answers { savedReply(firstArg(), "Answer") }
        coEvery { api.chats(any(), any()) } answers { savedChats.values.toList().reversed() }
    }

    private fun savedReply(request: ChatRequest, text: String): ChatResponse {
        val id = if (request.sessionId == 0) savedChats.size + 1 else request.sessionId
        savedChats[id] = ChatSummary(id, "Saved chat",
            request.userBikeId?.let { GarageBike(it, 100 + it, "Make", "Model", 2024, "") }, request.isGeneral, "")
        return ChatResponse(text, id)
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
        coEvery { api.askMechanicBro(any(), any()) } answers { savedReply(firstArg(), "Reply") }
        val login = keep(AuthViewModel(auth, api))
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.selectGeneral()
        chat.sendMessage("My question")
        runCurrent()
        val history = chat.state.value.messages
        assertEquals(2, history.size)

        statuses.value = SessionStatus.Initializing
        runCurrent()
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertFalse(login.state.value.sessionReady)
        assertEquals(history, chat.state.value.messages)

        statuses.value = SessionStatus.RefreshFailure(RefreshFailureCause.NetworkError(IOException("Offline")))
        runCurrent()
        assertFalse(login.state.value.loading)
        assertTrue(login.state.value.signedIn)
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertNotNull(login.state.value.error)
        assertEquals(history, chat.state.value.messages)

        statuses.value = authenticated("user-a", "refreshed-token")
        runCurrent()
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertTrue(login.state.value.sessionReady)
        assertEquals(history, chat.state.value.messages)
        assertTrue(chat.state.value.isGeneral)
        assertTrue(chat.state.value.selectionLocked)
        statuses.value = SessionStatus.NotAuthenticated(true)
        runCurrent()
        assertTrue(chat.state.value.messages.isEmpty())
    }

    @Test
    fun draftAndCameraResultSurviveBackgroundButNeverCarryToAnotherAccount() = runTest(dispatcher) {
        val login = keep(AuthViewModel(auth, api))
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.selectGeneral()
        chat.updateDraftText("Photo wala question")
        statuses.value = SessionStatus.Initializing
        runCurrent()
        chat.attachPhoto("user-a", "camera-result")
        assertEquals("Photo wala question", chat.state.value.draft.text)
        assertEquals("camera-result", chat.state.value.draft.imageData)
        assertEquals("user-a", login.state.value.profile?.supabaseUserId)
        assertFalse(login.state.value.sessionReady)
        chat.sendDraft()
        runCurrent()
        coVerify(exactly = 0) { api.askMechanicBro(any(), any()) }

        statuses.value = authenticated("user-a", "new-token")
        runCurrent()
        assertTrue(login.state.value.sessionReady)
        assertEquals("camera-result", chat.state.value.draft.imageData)
        statuses.value = authenticated("user-b")
        runCurrent()
        assertFalse(chat.state.value.isGeneral)
        assertNull(chat.state.value.selectedBike)
        assertEquals("", chat.state.value.draft.text)
        assertNull(chat.state.value.draft.imageData)
        chat.attachPhoto("user-a", "late-camera-result")
        assertNull(chat.state.value.draft.imageData)
        chat.updateDraftText("User B draft")
        statuses.value = SessionStatus.NotAuthenticated(true)
        runCurrent()
        assertEquals("", chat.state.value.draft.text)
    }

    @Test
    fun reconnectingProfileFailureKeepsDraftAndDisablesSendingUntilVerified() = runTest(dispatcher) {
        val login = keep(AuthViewModel(auth, api))
        val chat = keep(ChatViewModel(auth, api, api))
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
        assertEquals("Keep this", chat.state.value.draft.text)
    }

    @Test
    fun accountSwitchCancelsOldProfileAndOldChatResponse() = runTest(dispatcher) {
        val oldProfile = CompletableDeferred<UserProfileResponse>()
        val oldReply = CompletableDeferred<ChatResponse>()
        coEvery { api.me("Bearer token-a") } coAnswers { oldProfile.await() }
        coEvery { api.askMechanicBro(any(), "Bearer token-a") } coAnswers { oldReply.await() }
        val login = keep(AuthViewModel(auth, api))
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a", "token-a")
        runCurrent()
        chat.selectGeneral()
        chat.sendMessage("Private message A")
        runCurrent()
        statuses.value = authenticated("user-b", "token-b")
        runCurrent()
        assertEquals("user-b", login.state.value.profile?.supabaseUserId)
        assertTrue(chat.state.value.messages.isEmpty())
        oldProfile.complete(UserProfileResponse(1, "user-a", "a@example.test", "User"))
        oldReply.complete(ChatResponse("Private answer A", 99))
        runCurrent()
        assertEquals("user-b", login.state.value.profile?.supabaseUserId)
        assertTrue(chat.state.value.messages.isEmpty())
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
    fun newChatAndRecentChatsPreserveTheirOwnLockedBikeAndMessages() = runTest(dispatcher) {
        coEvery { api.askMechanicBro(any(), any()) } answers { savedReply(firstArg(), "Answer") }
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        val bikes = listOf(
            GarageBike(1, 101, "Harley-Davidson", "X440", 2024, ""),
            GarageBike(2, 102, "Harley-Davidson", "X440", 2025, "")
        )
        chat.updateGarage(bikes)
        chat.selectBike(1)
        chat.updateDraftText("First question")
        chat.sendDraft()
        runCurrent()
        assertTrue(chat.state.value.selectionLocked)
        val firstId = requireNotNull(chat.state.value.chatId)
        chat.selectBike(2)
        assertEquals(1, chat.state.value.selectedBike?.id)
        assertNotNull(chat.state.value.selectionError)
        chat.newChat()
        assertFalse(chat.state.value.selectionLocked)
        assertNull(chat.state.value.selectedBike)
        chat.selectBike(2)
        chat.updateDraftText("Second question")
        chat.sendDraft()
        runCurrent()
        assertEquals(2, chat.state.value.recentChats.size)
        coVerify { api.askMechanicBro(match { it.sessionId == 0 && it.userBikeId == 2 }, any()) }
        coEvery { api.chat(firstId, any(), any()) } returns ChatDetail(
            savedChats.getValue(firstId).copy(bike = bikes[0]),
            listOf(SavedChatMessage(1, "First question", true, false), SavedChatMessage(2, "Answer", false, false)), null)
        chat.openChat(firstId)
        runCurrent()
        assertEquals("First question", chat.state.value.messages.first().text)
        assertEquals(1, chat.state.value.selectedBike?.id)
        assertTrue(chat.state.value.selectionLocked)
        chat.updateGarage(listOf(bikes[1]))
        assertEquals(1, chat.state.value.selectedBike?.id)
        statuses.value = SessionStatus.NotAuthenticated(true)
        runCurrent()
        assertTrue(chat.state.value.recentChats.isEmpty())
        assertNull(chat.state.value.selectedBike)
    }

    @Test
    fun sidebarPageChangeCannotLoseDraftAndRemovedUnsentBikeCannotBeSelected() = runTest(dispatcher) {
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        val bike = GarageBike(1, 101, "Make", "Model", 2024, "")
        chat.updateGarage(listOf(bike))
        chat.selectBike(1)
        chat.updateDraftText("Keep draft while I visit garage")
        chat.attachPhoto("user-a", "photo")
        chat.updateGarage(emptyList())
        assertNull(chat.state.value.selectedBike)
        assertEquals("photo", chat.state.value.draft.imageData)
        chat.sendDraft()
        runCurrent()
        assertNotNull(chat.state.value.selectionError)
        coVerify(exactly = 0) { api.askMechanicBro(any(), any()) }
    }

    @Test
    fun newChatRequiresExplicitSelectionAndGeneralWorksWithoutAGarage() = runTest(dispatcher) {
        coEvery { api.askMechanicBro(any(), any()) } answers { savedReply(firstArg(), "General answer") }
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        assertFalse(chat.state.value.isGeneral)
        assertNull(chat.state.value.selectedBike)

        chat.updateDraftText("Hi")
        chat.sendDraft()
        chat.sendMessage("Another attempt")
        chat.attachPhoto("user-a", "must-not-attach")
        runCurrent()
        assertNotNull(chat.state.value.selectionError)
        assertNull(chat.state.value.draft.imageData)
        assertTrue(chat.state.value.messages.isEmpty())
        assertFalse(chat.state.value.selectionLocked)
        coVerify(exactly = 0) { api.askMechanicBro(any(), any()) }

        chat.selectGeneral()
        chat.updateGarage(emptyList())
        assertTrue(chat.state.value.isGeneral)
        assertNull(chat.state.value.selectionError)
        chat.attachPhoto("user-a", "general-photo")
        chat.sendDraft()
        runCurrent()
        assertTrue(chat.state.value.selectionLocked)
        assertEquals("General", chat.state.value.recentChats.single().bikeLabel)
        coVerify(exactly = 1) {
            api.askMechanicBro(match { it.sessionId == 0 && it.imageData == "general-photo" && it.isGeneral && it.userBikeId == null }, any())
        }
        assertEquals(1, chat.state.value.chatId)
    }

    @Test
    fun generalAndBikeSelectionsRemainSeparateAndLockAcrossRecentChats() = runTest(dispatcher) {
        coEvery { api.askMechanicBro(any(), any()) } answers { savedReply(firstArg(), "Answer") }
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.updateGarage(listOf(GarageBike(1, 101, "Make", "Model", 2024, "")))
        chat.selectGeneral()
        chat.selectBike(1)
        assertFalse(chat.state.value.isGeneral)
        chat.selectGeneral()
        assertNull(chat.state.value.selectedBike)
        chat.updateDraftText("General question")
        chat.sendDraft()
        runCurrent()
        val generalId = requireNotNull(chat.state.value.chatId)
        chat.selectBike(1)
        assertTrue(chat.state.value.isGeneral)
        assertNull(chat.state.value.selectedBike)
        assertNotNull(chat.state.value.selectionError)

        chat.newChat()
        assertFalse(chat.state.value.isGeneral)
        assertNull(chat.state.value.selectedBike)
        assertFalse(chat.state.value.selectionLocked)
        chat.sendMessage("Must select again")
        runCurrent()
        assertTrue(chat.state.value.messages.isEmpty())
        coVerify(exactly = 1) { api.askMechanicBro(any(), any()) }
        chat.selectBike(1)
        chat.updateDraftText("Bike question")
        chat.sendDraft()
        runCurrent()
        chat.selectGeneral()
        assertFalse(chat.state.value.isGeneral)
        assertEquals(1, chat.state.value.selectedBike?.id)
        coVerify { api.askMechanicBro(match { it.sessionId == 0 && !it.isGeneral && it.userBikeId == 1 }, any()) }

        coEvery { api.chat(generalId, any(), any()) } returns ChatDetail(
            savedChats.getValue(generalId),
            listOf(SavedChatMessage(1, "General question", true, false), SavedChatMessage(2, "Answer", false, false)), null)
        chat.openChat(generalId)
        runCurrent()
        assertTrue(chat.state.value.isGeneral)
        assertNull(chat.state.value.selectedBike)
        assertTrue(chat.state.value.selectionLocked)
        assertEquals("General question", chat.state.value.messages.first().text)
        statuses.value = SessionStatus.NotAuthenticated(true)
        runCurrent()
        assertFalse(chat.state.value.isGeneral)
        assertNull(chat.state.value.selectedBike)
        assertTrue(chat.state.value.recentChats.isEmpty())
    }

    @Test
    fun failedGeneralAnswerStillLocksSelection() = runTest(dispatcher) {
        coEvery { api.askMechanicBro(any(), any()) } throws IOException("Offline")
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.updateGarage(listOf(GarageBike(1, 101, "Make", "Model", 2024, "")))
        chat.selectGeneral()
        chat.sendMessage("General question")
        runCurrent()
        assertFalse(chat.state.value.loading)
        assertTrue(chat.state.value.selectionLocked)
        chat.selectBike(1)
        assertTrue(chat.state.value.isGeneral)
        assertNull(chat.state.value.selectedBike)
        assertNotNull(chat.state.value.selectionError)
        coVerify(exactly = 1) { api.askMechanicBro(match { it.sessionId == 0 && it.isGeneral }, any()) }
    }

    @Test
    fun firstReplyIdIsUsedForFollowUpsAndNewChatResetsItToZero() = runTest(dispatcher) {
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.selectGeneral()
        chat.sendMessage("First")
        runCurrent()
        assertEquals(1, chat.state.value.chatId)
        chat.sendMessage("Follow-up")
        runCurrent()
        coVerify(exactly = 1) { api.askMechanicBro(match { it.sessionId == 0 && it.message == "First" }, any()) }
        coVerify(exactly = 1) { api.askMechanicBro(match { it.sessionId == 1 && it.message == "Follow-up" }, any()) }
        assertEquals(1, savedChats.size)
        chat.newChat()
        assertNull(chat.state.value.chatId)
        chat.selectGeneral()
        chat.sendMessage("New")
        runCurrent()
        coVerify { api.askMechanicBro(match { it.sessionId == 0 && it.message == "New" }, any()) }
        assertEquals(2, chat.state.value.chatId)
    }

    @Test
    fun failedFirstReplyRetainsServerIdForRetry() = runTest(dispatcher) {
        coEvery { api.askMechanicBro(any(), any()) } throws HttpException(Response.error<ChatResponse>(500,
            """{"error":"Provider failed","sessionId":41}""".toResponseBody("application/json".toMediaType())))
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.selectGeneral()
        chat.sendMessage("First")
        runCurrent()
        assertEquals(41, chat.state.value.chatId)
        assertTrue(chat.state.value.selectionLocked)
        coEvery { api.askMechanicBro(any(), any()) } returns ChatResponse("Recovered", 41)
        chat.sendMessage("Retry")
        runCurrent()
        coVerify { api.askMechanicBro(match { it.sessionId == 41 && it.message == "Retry" }, any()) }
        assertEquals("Recovered", chat.state.value.messages.last().text)
    }

    @Test
    fun delayedFirstReplyCannotAssignItsIdToANewerChat() = runTest(dispatcher) {
        val pending = CompletableDeferred<ChatResponse>()
        coEvery { api.askMechanicBro(any(), any()) } coAnswers { withContext(NonCancellable) { pending.await() } }
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.selectGeneral()
        chat.sendMessage("Old")
        runCurrent()
        chat.newChat()
        pending.complete(ChatResponse("Old answer", 87))
        runCurrent()
        assertNull(chat.state.value.chatId)
        assertTrue(chat.state.value.messages.isEmpty())
        assertFalse(chat.state.value.isGeneral)
    }

    @Test
    fun malformedFailureDoesNotInventAChatIdAndShowsReloadGuidance() = runTest(dispatcher) {
        coEvery { api.askMechanicBro(any(), any()) } throws HttpException(Response.error<ChatResponse>(502,
            "<html>Proxy failure</html>".toResponseBody("text/html".toMediaType())))
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.selectGeneral()
        chat.sendMessage("First")
        runCurrent()
        assertNull(chat.state.value.chatId)
        assertNotNull(chat.state.value.selectionError)
        assertFalse(chat.state.value.loading)
    }

    @Test
    fun mismatchedResponseCannotReplaceAnExistingChatId() = runTest(dispatcher) {
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.selectGeneral()
        chat.sendMessage("First")
        runCurrent()
        assertEquals(1, chat.state.value.chatId)
        coEvery { api.askMechanicBro(any(), any()) } returns ChatResponse("Wrong chat answer", 99)
        chat.sendMessage("Follow-up")
        runCurrent()
        assertEquals(1, chat.state.value.chatId)
        assertFalse(chat.state.value.messages.any { it.text == "Wrong chat answer" })
        assertFalse(chat.state.value.loading)
    }

    @Test
    fun garageLoadsCatalogAddsOnlyKnownBikeAndDeletesByGarageEntryId() = runTest(dispatcher) {
        val catalog = listOf(CatalogBike(101, "Make", "Model", 2024), CatalogBike(102, "Make", "Model", 2025))
        val added = GarageBike(7, 102, "Make", "Model", 2025, "")
        coEvery { api.bikes(any()) } returns catalog
        coEvery { api.garage(any()) } returns emptyList()
        coEvery { api.addBike(AddGarageBikeRequest(102), any()) } returns added
        coEvery { api.deleteBike(7, any()) } returns Unit
        val garage = keep(GarageViewModel(auth, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        assertEquals(catalog, garage.state.value.catalog)
        garage.addBike(999)
        runCurrent()
        assertNotNull(garage.state.value.error)
        coVerify(exactly = 0) { api.addBike(any(), any()) }
        garage.addBike(102)
        runCurrent()
        assertEquals(listOf(added), garage.state.value.bikes)
        garage.deleteBike(7)
        runCurrent()
        assertTrue(garage.state.value.bikes.isEmpty())
        coVerify { api.deleteBike(7, any()) }
        statuses.value = SessionStatus.NotAuthenticated(true)
        runCurrent()
        assertTrue(garage.state.value.catalog.isEmpty())
    }

    @Test
    fun cancelledGarageLoadCannotPopulateAnotherAccountsSidebar() = runTest(dispatcher) {
        val old = CompletableDeferred<List<GarageBike>>()
        coEvery { api.bikes(any()) } returns listOf(CatalogBike(101, "Make", "Model", 2024))
        coEvery { api.garage("Bearer token-a") } coAnswers { old.await() }
        coEvery { api.garage("Bearer token-b") } returns emptyList()
        val garage = keep(GarageViewModel(auth, api))
        statuses.value = authenticated("user-a", "token-a")
        runCurrent()
        statuses.value = authenticated("user-b", "token-b")
        runCurrent()
        old.complete(listOf(GarageBike(1, 101, "Old", "Private", 2024, "")))
        runCurrent()
        assertTrue(garage.state.value.bikes.isEmpty())
        assertFalse(garage.state.value.loading)
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
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.selectGeneral()
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
        assertTrue(chat.state.value.loading)
        nextReply.complete(ChatResponse("Second answer", 1))
        runCurrent()
        assertFalse(chat.state.value.loading)
        assertEquals("Second answer", chat.state.value.messages.last().text)
    }

    @Test
    fun freshViewModelReopensDatabaseChatAndContinuesWithoutCreatingAnotherChat() = runTest(dispatcher) {
        val summary = ChatSummary(77, "Saved before restart", null, true, "")
        coEvery { api.chats(any(), any()) } returns listOf(summary)
        coEvery { api.chat(77, any(), null) } returns ChatDetail(summary,
            listOf(SavedChatMessage(3, "Saved photo question", true, true), SavedChatMessage(4, "Saved answer", false, false)), 3)
        coEvery { api.chat(77, any(), 3) } returns ChatDetail(summary,
            listOf(SavedChatMessage(1, "Earlier question", true, false), SavedChatMessage(2, "Earlier answer", false, false)), null)
        coEvery { api.askMechanicBro(any(), any()) } returns ChatResponse("New answer", 77)
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        assertEquals(77, chat.state.value.recentChats.single().id)
        assertNull(chat.state.value.selectedBike)
        assertFalse(chat.state.value.isGeneral)
        chat.openChat(77)
        runCurrent()
        assertTrue(chat.state.value.isGeneral)
        assertTrue(chat.state.value.selectionLocked)
        assertTrue(chat.state.value.messages.first().photoNotStored)
        assertTrue(chat.state.value.hasOlderMessages)
        chat.loadOlderMessages()
        runCurrent()
        assertEquals("Earlier question", chat.state.value.messages.first().text)
        assertFalse(chat.state.value.hasOlderMessages)
        chat.sendMessage("Follow-up")
        runCurrent()
        coVerify(exactly = 1) { api.askMechanicBro(match { it.sessionId == 77 && it.message == "Follow-up" }, any()) }
    }

    @Test
    fun delayedDatabaseChatCannotPopulateAnotherAccountAndListErrorsAreVisible() = runTest(dispatcher) {
        val pending = CompletableDeferred<ChatDetail>()
        coEvery { api.chat(77, any(), any()) } coAnswers { withContext(NonCancellable) { pending.await() } }
        val chat = keep(ChatViewModel(auth, api, api))
        statuses.value = authenticated("user-a")
        runCurrent()
        chat.openChat(77)
        runCurrent()
        coEvery { api.chats(any(), any()) } throws IOException("Database unavailable")
        statuses.value = authenticated("user-b")
        runCurrent()
        pending.complete(ChatDetail(ChatSummary(77, "Private", null, true, ""),
            listOf(SavedChatMessage(1, "Private message", true, false)), null))
        runCurrent()
        assertTrue(chat.state.value.messages.isEmpty())
        assertFalse(chat.state.value.isGeneral)
        assertNotNull(chat.state.value.historyError)
        coEvery { api.chats(any(), any()) } returns emptyList()
        chat.refreshRecent()
        runCurrent()
        assertNull(chat.state.value.historyError)
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
