package com.example.ridefixbro

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.width
import androidx.compose.material3.MaterialTheme
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import androidx.compose.ui.unit.dp
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.ui.pages.Sidebar
import com.example.ridefixbro.viewmodel.ChatUiState
import com.example.ridefixbro.viewmodel.RecentChat
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test

class WorkspaceSidebarTest {
    @get:Rule val compose = createComposeRule()

    @Test
    fun regularUserDoesNotSeeAdminPublicationAndDeleteReportsTheSelectedChat() {
        var selected: Int? = null
        showSidebar("User") { selected = it }
        compose.onNodeWithText("Publish bike/manual").assertDoesNotExist()
        compose.onNodeWithContentDescription("Delete chat Saved").performClick()
        compose.runOnIdle { assertEquals(7, selected) }
    }

    @Test
    fun adminSeesThePublicationEntry() {
        showSidebar("Admin") {}
        compose.onNodeWithText("Publish bike/manual").assertExists()
    }

    private fun showSidebar(role: String, onDelete: (Int) -> Unit) {
        compose.setContent {
            MaterialTheme {
                Box(Modifier.width(300.dp).height(800.dp)) {
                    Sidebar(true, UserProfileResponse(1, "user-a", "a@example.test", role),
                        ChatUiState(recentChats = listOf(RecentChat(7, "Saved", "General", "2026-09-29T10:00:00Z"))),
                        enabled = true, onToggle = {}, onNewChat = {}, onOpenChat = {},
                        onAddBike = {}, onGarage = {}, onProfile = {}, onSignOut = {},
                        onRefreshChats = {}, onMoreChats = {}, onDeleteChat = { onDelete(it.id) }, onAdmin = {})
                }
            }
        }
    }
}
