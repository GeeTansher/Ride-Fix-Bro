package com.example.ridefixbro.ui.pages.chatscreen

import android.graphics.Bitmap
import android.util.Base64
import java.io.ByteArrayOutputStream

fun encodeBitmapToBase64(bitmap: Bitmap): String {
    val outputStream = ByteArrayOutputStream()
    // 70% quality pe compress kar rahe hain taaki payload lamba na ho aur API jaldi reply kare
    bitmap.compress(Bitmap.CompressFormat.JPEG, 70, outputStream)
    val byteArray = outputStream.toByteArray()
    // No wrap zaroori hai taaki extra line breaks na aayein
    return Base64.encodeToString(byteArray, Base64.NO_WRAP)
}