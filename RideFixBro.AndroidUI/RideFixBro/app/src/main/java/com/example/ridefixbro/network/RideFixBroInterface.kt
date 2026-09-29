package com.example.ridefixbro.network

import com.example.ridefixbro.model.request.ChatRequest
import com.example.ridefixbro.model.response.ChatResponse
import com.example.ridefixbro.model.response.UserProfileResponse
import retrofit2.http.Body
import retrofit2.http.POST
import retrofit2.http.GET
import retrofit2.http.Header
import retrofit2.http.DELETE
import retrofit2.http.Path
import com.example.ridefixbro.model.CatalogBike
import com.example.ridefixbro.model.GarageBike
import com.example.ridefixbro.model.AddGarageBikeRequest
import com.example.ridefixbro.model.ChatSummary
import com.example.ridefixbro.model.ChatDetail
import retrofit2.http.Query
import retrofit2.http.Multipart
import retrofit2.http.Part
import okhttp3.MultipartBody
import okhttp3.RequestBody
import com.example.ridefixbro.model.BikePublicationResponse

interface RideFixApiInterface {
    @GET("api/chats")
    suspend fun chats(@Header("Authorization") authorization: String, @Query("beforeId") beforeId: Int? = null,
        @Query("beforeUpdatedAt") beforeUpdatedAt: String? = null): List<ChatSummary>

    @GET("api/chats/{id}")
    suspend fun chat(@Path("id") id: Int, @Header("Authorization") authorization: String,
        @Query("beforeSequence") beforeSequence: Int? = null): ChatDetail

    @DELETE("api/chats/{id}")
    suspend fun deleteChat(@Path("id") id: Int, @Header("Authorization") authorization: String)

    @Multipart
    @POST("api/admin/bikes")
    suspend fun publishBike(
        @Part("make") make: RequestBody,
        @Part("model") model: RequestBody,
        @Part("year") year: RequestBody,
        @Part("manualKey") manualKey: RequestBody,
        @Part("skipPages") skipPages: RequestBody,
        @Part file: MultipartBody.Part,
        @Header("Authorization") authorization: String
    ): BikePublicationResponse

    // Ye apne .NET backend ka endpoint hai
    @POST("api/Chat/ask")
    suspend fun askMechanicBro(
        @Body request: ChatRequest,
        @Header("Authorization") authorization: String
    ): ChatResponse

    @GET("api/me")
    suspend fun me(@Header("Authorization") authorization: String): UserProfileResponse

    @GET("api/bikes")
    suspend fun bikes(@Header("Authorization") authorization: String): List<CatalogBike>

    @GET("api/garage")
    suspend fun garage(@Header("Authorization") authorization: String): List<GarageBike>

    @POST("api/garage")
    suspend fun addBike(
        @Body request: AddGarageBikeRequest,
        @Header("Authorization") authorization: String
    ): GarageBike

    @DELETE("api/garage/{id}")
    suspend fun deleteBike(@Path("id") id: Int, @Header("Authorization") authorization: String)
}