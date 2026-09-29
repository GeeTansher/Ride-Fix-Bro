package com.example.ridefixbro.model

data class ChatSummary(val id: Int, val title: String, val bike: GarageBike?, val isGeneral: Boolean, val updatedAt: String)
data class SavedChatMessage(val sequenceNumber: Int, val text: String, val isUser: Boolean, val photoNotStored: Boolean)
data class ChatDetail(val chat: ChatSummary, val messages: List<SavedChatMessage>, val nextBeforeSequence: Int?)
