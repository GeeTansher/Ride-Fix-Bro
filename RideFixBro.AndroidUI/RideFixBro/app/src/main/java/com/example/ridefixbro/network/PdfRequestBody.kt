package com.example.ridefixbro.network

import android.content.ContentResolver
import android.net.Uri
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.RequestBody
import okio.BufferedSink
import java.io.IOException

class PdfRequestBody(private val resolver: ContentResolver, private val uri: Uri) : RequestBody() {
    override fun contentType() = "application/pdf".toMediaType()
    override fun isOneShot() = true

    override fun writeTo(sink: BufferedSink) {
        val input = resolver.openInputStream(uri) ?: throw IOException("Selected PDF cannot be opened.")
        input.use {
            val buffer = ByteArray(8192)
            var total = 0L
            while (true) {
                val count = it.read(buffer)
                if (count < 0) break
                total += count
                if (total > 20L * 1024 * 1024) throw IOException("PDF exceeds 20 MiB.")
                sink.write(buffer, 0, count)
            }
            if (total == 0L) throw IOException("Selected PDF is empty.")
        }
    }
}
