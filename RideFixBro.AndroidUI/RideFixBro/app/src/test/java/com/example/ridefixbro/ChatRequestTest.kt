package com.example.ridefixbro

import com.example.ridefixbro.model.request.ChatRequest
import com.google.gson.Gson
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ChatRequestTest {
    @Test
    fun serializesSessionIdForInitialAndFollowUpMessages() {
        val first = ChatRequest(sessionId = 0, message = "First question", isGeneral = true)
        val followUp = first.copy(sessionId = 42, message = "Follow-up question", imageData = "image")
        val gson = Gson()

        val firstJson = gson.toJsonTree(first).asJsonObject
        val followUpJson = gson.toJsonTree(followUp).asJsonObject

        assertTrue(firstJson.getAsJsonPrimitive("sessionId").isNumber)
        assertTrue(followUpJson.getAsJsonPrimitive("sessionId").isNumber)
        assertEquals(0, firstJson.get("sessionId").asInt)
        assertEquals(42, followUpJson.get("sessionId").asInt)
        assertEquals("Follow-up question", followUpJson.get("message").asString)
        assertEquals("image", followUpJson.get("imageData").asString)
    }

    @Test
    fun serializesExplicitGeneralSeparatelyFromAGarageBike() {
        val gson = Gson()
        val general = gson.toJsonTree(
            ChatRequest(sessionId = 0, message = "Hi", isGeneral = true)
        ).asJsonObject
        assertTrue(general.get("isGeneral").asBoolean)
        assertFalse(general.has("userBikeId"))

        val bike = gson.toJsonTree(
            ChatRequest(sessionId = 0, message = "Hi", userBikeId = 7)
        ).asJsonObject
        assertFalse(bike.get("isGeneral").asBoolean)
        assertEquals(7, bike.get("userBikeId").asInt)
    }
}
