package com.example.ridefixbro

import com.example.ridefixbro.model.request.ChatRequest
import com.google.gson.Gson
import org.junit.Assert.assertEquals
import org.junit.Test

class ChatRequestTest {
    @Test
    fun serializesSessionIdForInitialAndFollowUpMessages() {
        val first = ChatRequest(sessionId = "chat-session", message = "First question")
        val followUp = first.copy(message = "Follow-up question", imageData = "image")
        val gson = Gson()

        val firstJson = gson.toJsonTree(first).asJsonObject
        val followUpJson = gson.toJsonTree(followUp).asJsonObject

        assertEquals("chat-session", firstJson.get("sessionId").asString)
        assertEquals("chat-session", followUpJson.get("sessionId").asString)
        assertEquals("Follow-up question", followUpJson.get("message").asString)
        assertEquals("image", followUpJson.get("imageData").asString)
    }
}
