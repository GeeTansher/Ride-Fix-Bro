package com.example.ridefixbro.network

import com.example.ridefixbro.model.request.ChatRequest
import com.example.ridefixbro.model.response.ChatResponse
import com.example.ridefixbro.model.response.UserProfileResponse
import retrofit2.http.Body
import retrofit2.http.POST
import retrofit2.http.GET
import retrofit2.http.Header

interface RideFixApiInterface {
    // Ye apne .NET backend ka endpoint hai
    @POST("api/Chat/ask")
    suspend fun askMechanicBro(
        @Body request: ChatRequest,
        @Header("Authorization") authorization: String
    ): ChatResponse

    @GET("api/me")
    suspend fun me(@Header("Authorization") authorization: String): UserProfileResponse
}