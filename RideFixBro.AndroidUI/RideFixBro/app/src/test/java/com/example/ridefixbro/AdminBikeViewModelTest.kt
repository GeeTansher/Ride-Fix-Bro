package com.example.ridefixbro

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.ViewModelStore
import com.example.ridefixbro.auth.AuthRepository
import com.example.ridefixbro.model.ManualPublicationJob
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.network.RideFixApiInterface
import com.example.ridefixbro.viewmodel.AdminBikeForm
import com.example.ridefixbro.viewmodel.AdminBikeViewModel
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
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.setMain
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okio.Buffer
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import java.io.IOException
import kotlin.time.Clock
import kotlin.time.Duration.Companion.seconds

@OptIn(ExperimentalCoroutinesApi::class, kotlin.time.ExperimentalTime::class)
class AdminBikeViewModelTest {
    private val dispatcher = StandardTestDispatcher()
    private val store = ViewModelStore()
    private val statuses = MutableStateFlow<SessionStatus>(SessionStatus.Initializing)
    private val auth = mockk<AuthRepository>()
    private val api = mockk<RideFixApiInterface>()
    private val admin = UserProfileResponse(1, "user-a", "a@example.test", "Admin")
    private val form = AdminBikeForm(" Harley-Davidson ", " X440 ", "2024", " x440-2024 ", "1")
    private val queued = job("Queued")
    private val completed = job("Succeeded")
    private val file = MultipartBody.Part.createFormData("file", "manual.pdf",
        "%PDF-test".toRequestBody("application/pdf".toMediaType()))

    @Before
    fun setup() {
        Dispatchers.setMain(dispatcher)
        every { auth.sessionStatus } returns statuses
        coEvery { auth.accessToken(any()) } returns "token"
        coEvery { api.publishBike(any(), any(), any(), any(), any(), any(), any()) } returns queued
        coEvery { api.manualJobs(any()) } returns emptyList()
        coEvery { api.manualJob(any(), any()) } returns completed
    }

    @After
    fun cleanup() {
        store.clear()
        Dispatchers.resetMain()
    }

    @Test
    fun nonAdminAndIncompleteFormsNeverReachTheApi() = runTest(dispatcher) {
        val model = model()
        statuses.value = authenticated("user-a")
        runCurrent()
        model.updateForm(form)
        model.selectPdf("user-a", "content://test/manual", "manual.pdf")
        model.publish(admin.copy(role = "User"), file)
        assertNotNull(model.state.value.error)
        model.updateForm(form.copy(manualKey = ""))
        model.publish(admin, file)
        model.updateForm(form)
        model.publish(admin, null)
        runCurrent()
        coVerify(exactly = 0) { api.publishBike(any(), any(), any(), any(), any(), any(), any()) }
    }

    @Test
    fun acceptedSubmissionIsNotSuccessUntilStatusReportsCompletion() = runTest(dispatcher) {
        val pending = CompletableDeferred<ManualPublicationJob>()
        coEvery { api.publishBike(any(), any(), any(), any(), any(), any(), any()) } coAnswers { pending.await() }
        val model = model()
        statuses.value = authenticated("user-a")
        runCurrent()
        model.observeJobs(admin)
        runCurrent()
        model.updateForm(form)
        model.selectPdf("user-a", "content://test/manual", "manual.pdf")
        model.publish(admin, file)
        runCurrent()
        assertTrue(model.state.value.loading)
        assertNull(model.state.value.job)
        coEvery { api.manualJobs(any()) } returns listOf(queued)
        pending.complete(queued)
        runCurrent()
        assertFalse(model.state.value.loading)
        assertEquals(queued, model.state.value.job)
        assertTrue(model.state.value.busy)
        assertFalse(model.state.value.job!!.succeeded)
        advanceTimeBy(3_000)
        runCurrent()
        assertEquals(completed, model.state.value.job)
        assertFalse(model.state.value.busy)
        coVerify(exactly = 1) {
            api.publishBike(match { text(it) == "Harley-Davidson" }, match { text(it) == "X440" },
                match { text(it) == "2024" }, match { text(it) == "x440-2024" }, match { text(it) == "1" }, file, any())
        }
    }

    @Test
    fun failedPublicationNeverShowsACompletedBike() = runTest(dispatcher) {
        coEvery { api.publishBike(any(), any(), any(), any(), any(), any(), any()) } throws IOException("Offline")
        val model = model()
        statuses.value = authenticated("user-a")
        runCurrent()
        model.updateForm(form)
        model.selectPdf("user-a", "content://test/manual", "manual.pdf")
        model.publish(admin, file)
        runCurrent()
        assertFalse(model.state.value.loading)
        assertNull(model.state.value.job)
        assertNotNull(model.state.value.error)
    }

    @Test
    fun lateUploadResultAndPdfCannotPopulateAnotherAccount() = runTest(dispatcher) {
        val pending = CompletableDeferred<ManualPublicationJob>()
        coEvery { api.publishBike(any(), any(), any(), any(), any(), any(), any()) } coAnswers {
            withContext(NonCancellable) { pending.await() }
        }
        val model = model()
        statuses.value = authenticated("user-a")
        runCurrent()
        model.updateForm(form)
        model.selectPdf("user-a", "content://test/manual", "manual.pdf")
        model.publish(admin, file)
        runCurrent()
        statuses.value = authenticated("user-b")
        runCurrent()
        pending.complete(queued)
        model.selectPdf("user-a", "content://test/late", "old.pdf")
        runCurrent()
        assertNull(model.state.value.job)
        assertNull(model.state.value.pdfUri)
        assertEquals(AdminBikeForm(), model.state.value.form)
        assertFalse(model.state.value.loading)
    }

    @Test
    fun authSetupFailureIsVisibleInsteadOfAnUncaughtCoroutineError() = runTest(dispatcher) {
        every { auth.sessionStatus } returns flow { throw IOException("Setup unavailable") }
        val model = model()
        runCurrent()
        assertNotNull(model.state.value.error)
    }

    @Test
    fun reopeningAdminScreenRecoversActiveJobWithoutUploadingAgain() = runTest(dispatcher) {
        coEvery { api.manualJobs(any()) } returns listOf(job("Processing", completedChunks = 2))
        val model = model()
        statuses.value = authenticated("user-a")
        runCurrent()
        model.observeJobs(admin)
        runCurrent()
        assertEquals(2, model.state.value.job?.completedChunks)
        coVerify(exactly = 0) { api.publishBike(any(), any(), any(), any(), any(), any(), any()) }
        model.stopObserving()
        advanceTimeBy(9_000)
        runCurrent()
        coVerify(exactly = 0) { api.manualJob(any(), any()) }
        model.observeJobs(admin)
        runCurrent()
        advanceTimeBy(3_000)
        runCurrent()
        assertTrue(model.state.value.job!!.succeeded)
    }

    @Test
    fun failedJobShowsServerErrorAndNeverMarksCatalogPublicationSuccessful() = runTest(dispatcher) {
        coEvery { api.manualJobs(any()) } returns listOf(queued)
        coEvery { api.manualJob(any(), any()) } returns job("Failed", error = "Provider quota exhausted.")
        val model = model()
        statuses.value = authenticated("user-a")
        runCurrent()
        model.observeJobs(admin)
        runCurrent()
        advanceTimeBy(3_000)
        runCurrent()
        assertTrue(model.state.value.job!!.finished)
        assertFalse(model.state.value.job!!.succeeded)
        assertEquals("Provider quota exhausted.", model.state.value.job?.error)
        advanceTimeBy(9_000)
        runCurrent()
        coVerify(exactly = 1) { api.manualJob(any(), any()) }
    }

    @Test
    fun regularUserDoesNotLoadAdminJobs() = runTest(dispatcher) {
        val model = model()
        statuses.value = authenticated("user-a")
        runCurrent()
        model.observeJobs(admin.copy(role = "User"))
        runCurrent()
        coVerify(exactly = 0) { api.manualJobs(any()) }
    }

    @Test
    fun lateStatusCannotPopulateAnotherAccount() = runTest(dispatcher) {
        val pending = CompletableDeferred<ManualPublicationJob>()
        coEvery { api.manualJobs(any()) } returns listOf(queued)
        coEvery { api.manualJob(any(), any()) } coAnswers { withContext(NonCancellable) { pending.await() } }
        val model = model()
        statuses.value = authenticated("user-a")
        runCurrent()
        model.observeJobs(admin)
        runCurrent()
        advanceTimeBy(3_000)
        runCurrent()
        statuses.value = authenticated("user-b")
        runCurrent()
        pending.complete(completed)
        runCurrent()
        assertNull(model.state.value.job)
        assertTrue(model.state.value.jobs.isEmpty())
    }

    private fun job(status: String, completedChunks: Int = if (status == "Succeeded") 3 else 0, error: String? = null) =
        ManualPublicationJob("00000000-0000-0000-0000-000000000001", status, "Harley-Davidson", "X440", 2024,
            "x440-2024", "official", 1, 3, completedChunks, if (status == "Succeeded") 101 else null,
            error, "", "", if (status == "Succeeded" || status == "Failed") "" else null)

    private fun model(): AdminBikeViewModel = ViewModelProvider(store, object : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>): T = AdminBikeViewModel(auth, api, api) as T
    })[AdminBikeViewModel::class.java]

    private fun text(body: RequestBody): String = Buffer().also { body.writeTo(it) }.readUtf8()
    private fun authenticated(id: String) = SessionStatus.Authenticated(UserSession(
        accessToken = "token", refreshToken = "refresh", expiresIn = 3600, expiresAt = Clock.System.now() + 3600.seconds,
        tokenType = "bearer", user = UserInfo(id = id, aud = "authenticated")))
}
