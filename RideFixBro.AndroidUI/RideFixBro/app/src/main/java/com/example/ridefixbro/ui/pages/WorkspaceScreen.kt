package com.example.ridefixbro.ui.pages

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.AccountCircle
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ChatBubbleOutline
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.DirectionsBike
import androidx.compose.material.icons.filled.Logout
import androidx.compose.material.icons.filled.Menu
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.example.ridefixbro.model.response.UserProfileResponse
import com.example.ridefixbro.ui.pages.chatscreen.ChatScreen
import com.example.ridefixbro.viewmodel.AuthUiState
import com.example.ridefixbro.viewmodel.ChatViewModel
import com.example.ridefixbro.viewmodel.GarageViewModel
import com.example.ridefixbro.viewmodel.ChatUiState
import kotlinx.coroutines.launch

private enum class WorkspacePage { Chat, AddBike, Garage, Profile }

@Composable
fun WorkspaceScreen(
    profile: UserProfileResponse,
    authState: AuthUiState,
    chatViewModel: ChatViewModel,
    garageViewModel: GarageViewModel,
    onRetryAuth: () -> Unit,
    onSignOut: () -> Unit
) {
    val drawer = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()
    var page by rememberSaveable { mutableStateOf(WorkspacePage.Chat) }
    val garage by garageViewModel.state.collectAsState()
    val chat by chatViewModel.state.collectAsState()
    val enabled = authState.sessionReady && !authState.loading

    LaunchedEffect(garage.bikes) { chatViewModel.updateGarage(garage.bikes) }
    fun navigate(next: WorkspacePage) {
        page = next
        scope.launch { drawer.close() }
    }
    val newChat = {
        chatViewModel.newChat()
        navigate(WorkspacePage.Chat)
    }
    val openChat: (Int) -> Unit = { id ->
        chatViewModel.openChat(id)
        navigate(WorkspacePage.Chat)
    }
    BackHandler(enabled = page != WorkspacePage.Chat && drawer.isClosed) { page = WorkspacePage.Chat }

    ModalNavigationDrawer(
        drawerState = drawer,
        drawerContent = {
            ModalDrawerSheet(Modifier.width(280.dp)) {
                Sidebar(
                    expanded = true, profile = profile, chat = chat,
                    onToggle = { scope.launch { drawer.close() } }, onNewChat = newChat, onOpenChat = openChat,
                    onAddBike = { navigate(WorkspacePage.AddBike) }, onGarage = { navigate(WorkspacePage.Garage) },
                    onProfile = { navigate(WorkspacePage.Profile) }, onSignOut = onSignOut,
                    onRefreshChats = { chatViewModel.refreshRecent() }, onMoreChats = { chatViewModel.refreshRecent(true) }
                )
            }
        }
    ) {
        Row(Modifier.fillMaxSize().systemBarsPadding()) {
            Surface(Modifier.width(64.dp).fillMaxHeight(), color = MaterialTheme.colorScheme.surfaceVariant) {
                Sidebar(
                    expanded = false, profile = profile, chat = chat,
                    onToggle = { scope.launch { drawer.open() } }, onNewChat = newChat, onOpenChat = openChat,
                    onAddBike = { navigate(WorkspacePage.AddBike) }, onGarage = { navigate(WorkspacePage.Garage) },
                    onProfile = { navigate(WorkspacePage.Profile) }, onSignOut = onSignOut,
                    onRefreshChats = { chatViewModel.refreshRecent() }, onMoreChats = { chatViewModel.refreshRecent(true) }
                )
            }
            Column(Modifier.weight(1f).fillMaxHeight()) {
                if (authState.loading) Text("Session reconnect ho raha hai...", Modifier.padding(8.dp))
                authState.error?.let { Text(it, Modifier.padding(8.dp), color = MaterialTheme.colorScheme.error) }
                if (!authState.loading && !authState.sessionReady) {
                    TextButton(onClick = onRetryAuth) { Text("Retry connection") }
                }
                when (page) {
                    WorkspacePage.Chat -> ChatScreen(chatViewModel, profile.supabaseUserId, enabled, garage.bikes)
                    WorkspacePage.AddBike -> AddBikeScreen(garage, enabled, garageViewModel::addBike, garageViewModel::refresh)
                    WorkspacePage.Garage -> GarageScreen(garage, enabled, garageViewModel::deleteBike, garageViewModel::refresh)
                    WorkspacePage.Profile -> ProfileScreen(profile)
                }
            }
        }
    }
}

@Composable
private fun Sidebar(
    expanded: Boolean, profile: UserProfileResponse, chat: ChatUiState,
    onToggle: () -> Unit, onNewChat: () -> Unit, onOpenChat: (Int) -> Unit,
    onAddBike: () -> Unit, onGarage: () -> Unit, onProfile: () -> Unit, onSignOut: () -> Unit,
    onRefreshChats: () -> Unit, onMoreChats: () -> Unit
) {
    Column(Modifier.fillMaxSize().padding(8.dp)) {
        IconButton(onClick = onToggle) {
            Icon(if (expanded) Icons.Default.ChevronLeft else Icons.Default.Menu,
                if (expanded) "Collapse navigation" else "Expand navigation")
        }
        SidebarItem("New chat", Icons.Default.Add, expanded, onNewChat)
        if (expanded) {
            Text("Recent chats", style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(8.dp))
            Text("Saved chats", style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 8.dp))
            TextButton(onClick = onRefreshChats, enabled = !chat.recentLoading) { Text("Refresh chats") }
            chat.historyError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            if (chat.recentLoading) LinearProgressIndicator(Modifier.fillMaxWidth())
            LazyColumn(Modifier.weight(1f)) {
                items(chat.recentChats, key = { it.id }) { recent ->
                    NavigationDrawerItem(
                        selected = recent.id == chat.chatId,
                        onClick = { onOpenChat(recent.id) },
                        label = {
                            Column {
                                Text(recent.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                recent.bikeLabel?.let {
                                    Text(it, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                                }
                            }
                        }
                    )
                }
                if (chat.recentChats.isEmpty()) item { Text("No chats yet", Modifier.padding(8.dp)) }
                if (chat.moreChats) item { TextButton(onClick = onMoreChats, enabled = !chat.recentLoading) { Text("Load more chats") } }
            }
        } else {
            SidebarItem("Recent chats", Icons.Default.ChatBubbleOutline, false, onToggle)
            Spacer(Modifier.weight(1f))
        }
        SidebarItem("Your garage", Icons.Default.DirectionsBike, expanded, onGarage)
        SidebarItem("Add new bike", Icons.Default.Add, expanded, onAddBike)
        HorizontalDivider(Modifier.padding(vertical = 8.dp))
        SidebarItem("Profile", Icons.Default.AccountCircle, expanded, onProfile)
        if (expanded) {
            Text(profile.name?.takeIf { it.isNotBlank() } ?: "Your account",
                Modifier.padding(horizontal = 8.dp), maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(profile.email, Modifier.padding(horizontal = 8.dp), maxLines = 1,
                overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
        }
        SidebarItem("Sign out", Icons.Default.Logout, expanded, onSignOut)
    }
}

@Composable
private fun SidebarItem(label: String, icon: ImageVector, expanded: Boolean, onClick: () -> Unit) {
    if (expanded) {
        TextButton(onClick = onClick, modifier = Modifier.fillMaxWidth()) {
            Icon(icon, contentDescription = null)
            Spacer(Modifier.width(12.dp))
            Text(label, Modifier.weight(1f))
        }
    } else {
        IconButton(onClick = onClick) { Icon(icon, contentDescription = label) }
    }
}

@Composable
private fun ProfileScreen(profile: UserProfileResponse) {
    Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Text("Profile", style = MaterialTheme.typography.headlineMedium)
        Icon(Icons.Default.AccountCircle, contentDescription = null, modifier = Modifier.size(56.dp))
        Text("Name", style = MaterialTheme.typography.labelLarge)
        Text(profile.name?.takeIf { it.isNotBlank() } ?: "Name not provided")
        Text("Email", style = MaterialTheme.typography.labelLarge)
        Text(profile.email)
        Text("Role: ${profile.role}")
    }
}
