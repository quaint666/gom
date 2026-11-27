package io.github.quaint666.gom

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class OriginsTest {
    private val gom = Origins("https://gom.example", alsoAllow = "team.login.example")

    @Test fun only_gom_over_https_is_gom() {
        assertTrue(gom.isGom("https://gom.example/c/abc"))
        assertTrue(gom.isGom("https://GOM.example/"))
        assertFalse(gom.isGom("http://gom.example/"))
        assertFalse(gom.isGom("https://gom.example.evil.example/"))
        assertFalse(gom.isGom("https://evil.example/?https://gom.example"))
        assertFalse(gom.isGom("https://gom.example:8443/"))
        assertFalse(gom.isGom("javascript:alert(1)"))
        assertFalse(gom.isGom("not a url"))
    }

    @Test fun the_extra_host_loads_inside_but_is_not_gom() {
        assertTrue(gom.loadsInside("https://team.login.example/cdn-cgi/access/login"))
        assertFalse(gom.isGom("https://team.login.example/"))
        assertFalse(gom.loadsInside("http://team.login.example/"))
        assertFalse(gom.loadsInside("https://youtube.com/watch"))
        assertFalse(Origins("https://gom.example").loadsInside("https://team.login.example/"))
    }

    @Test fun only_gom_s_own_pages_get_the_microphone_and_nothing_else() {
        val mic = listOf("android.webkit.resource.AUDIO_CAPTURE")
        assertTrue(gom.mayUseMicrophone("https://gom.example", mic))
        assertFalse(gom.mayUseMicrophone("https://team.login.example", mic))   // the sign-in in front: inside, but never the microphone
        assertFalse(gom.mayUseMicrophone("https://evil.example", mic))
        assertFalse(gom.mayUseMicrophone("http://gom.example", mic))
        assertFalse(gom.mayUseMicrophone("https://gom.example", listOf("android.webkit.resource.VIDEO_CAPTURE")))
        assertFalse(gom.mayUseMicrophone("https://gom.example", mic + "android.webkit.resource.VIDEO_CAPTURE"))
    }

    @Test fun the_origin_and_pages_come_from_the_setting() {
        assertEquals("https://gom.example", gom.origin)
        assertEquals("https://gom.example:8443", Origins("https://Gom.Example:8443/x").origin)
        assertEquals("https://gom.example/c/abc", gom.page("/c/abc"))
        assertEquals("https://gom.example/", gom.page("//evil.example/x"))
        assertEquals("https://gom.example/", gom.page("https://evil.example/"))
        assertEquals("https://gom.example/", gom.page(null))
    }

    @Test fun no_address_or_plain_http_is_not_a_server() {
        assertFalse(Origins("").configured)
        assertFalse(Origins("http://gom.example").configured)
        assertFalse(Origins("").isGom("https://gom.example/"))
        assertTrue(gom.configured)
    }
}

class ProtocolTest {
    @Test fun the_pages_messages_are_read_and_nothing_else() {
        assertEquals(FromPage.Hello(1), FromPage.parse("""{"t":"hello","v":1}"""))
        assertEquals(FromPage.Hello(1, 5_000_000), FromPage.parse("""{"t":"hello","v":1,"images":5000000}"""))
        assertEquals(FromPage.Hello(1, 0), FromPage.parse("""{"t":"hello","v":1,"images":-3}"""))
        assertEquals(FromPage.Enable, FromPage.parse("""{"t":"enable"}"""))
        assertEquals(FromPage.Disable, FromPage.parse("""{"t":"disable"}"""))
        assertEquals(FromPage.Clear("c-1"), FromPage.parse("""{"t":"clear","c":"c-1"}"""))
        assertNull(FromPage.parse("""{"t":"clear"}"""))
        assertNull(FromPage.parse("""{"t":"call"}"""))
        val chat = "6f1e2d3c-4b5a-4968-8776-655443322110"
        assertEquals(FromPage.Call(chat, "Minh", "/api/avatars/a.webp"), FromPage.parse("""{"t":"call","c":"$chat","name":"Minh","avatar":"/api/avatars/a.webp"}"""))
        assertEquals(FromPage.Call(chat, "Minh", null), FromPage.parse("""{"t":"call","c":"$chat","name":"Minh","avatar":null}"""))
        assertEquals(FromPage.Call(chat, "Minh", null), FromPage.parse("""{"t":"call","c":"$chat","name":"Minh","avatar":"https://elsewhere.example/a.webp"}"""))
        assertNull(FromPage.parse("""{"t":"call","c":"../x","name":"Minh"}"""))
        assertNull(FromPage.parse("not json"))
        assertNull(FromPage.parse("[]"))
    }

    @Test fun the_theme_carries_two_colours_and_a_bad_one_is_none() {
        assertEquals(FromPage.Theme(0xFF3F6B57.toInt(), 0xFF121513.toInt()), FromPage.parse("""{"t":"theme","bar":"#3F6B57","page":"rgb(18, 21, 19)"}"""))
        assertEquals(FromPage.Theme(null, null), FromPage.parse("""{"t":"theme","bar":"red","page":""}"""))
    }

    @Test fun the_apps_answers_are_as_the_page_expects() {
        val hello = JSONObject(ToPage.hello("1.0.0", push = true, permission = "granted", fid = "fid-1"))
        assertEquals("hello", hello.getString("t"))
        assertEquals(1, hello.getInt("v"))
        assertEquals("fid-1", hello.getString("fid"))
        assertTrue(hello.getBoolean("push"))
        assertFalse(JSONObject(ToPage.hello("1.0.0", push = false, permission = "default", fid = null)).has("fid"))
        assertFalse(hello.has("calls"))   // a build that takes no calls says nothing of them, as an older app
        assertTrue(JSONObject(ToPage.hello("1.0.0", push = true, permission = "granted", fid = null, calls = true)).getBoolean("calls"))
        assertEquals("fid-2", JSONObject(ToPage.enabled("fid-2")).getString("fid"))
        assertEquals("fid", JSONObject(ToPage.fid("fid-3")).getString("t"))
        assertEquals("denied", JSONObject(ToPage.denied).getString("t"))
        assertEquals("failed", JSONObject(ToPage.failed).getString("t"))
    }
}

class ColorsTest {
    @Test fun css_colours_are_read() {
        assertEquals(0xFF3F6B57.toInt(), Colors.parse("#3f6b57"))
        assertEquals(0xFFF7F5EF.toInt(), Colors.parse("rgba(247, 245, 239, 1)"))
        assertNull(Colors.parse("rgb(300, 0, 0)"))
        assertNull(Colors.parse("#abc"))
        assertNull(Colors.parse("light-dark(#fff, #000)"))
    }

    @Test fun icons_go_dark_on_light_colours() {
        assertTrue(Colors.isLight(0xFFF7F5EF.toInt()))
        assertFalse(Colors.isLight(0xFF3F6B57.toInt()))
        assertFalse(Colors.isLight(0xFF121513.toInt()))
    }
}

class PushTest {
    private val c = "0b5c3a52-1111-4a2b-9c3d-123456789abc"
    private val m = "9f1e2d3c-2222-4b5a-8c7d-abcdef012345"

    @Test fun messages_recalls_and_owner_notices_are_read_with_their_word_address() {
        assertEquals("/api/push/notice?c=$c&m=$m", Push.parse(mapOf("t" to "message", "c" to c, "m" to m))!!.noticePath)
        assertEquals(Push.Recall(c, m), Push.parse(mapOf("t" to "recall", "c" to c, "m" to m)))
        assertEquals("/api/push/notice?owner=join", Push.parse(mapOf("t" to "owner", "kind" to "join"))!!.noticePath)
    }

    @Test fun calls_unknown_kinds_and_ids_that_are_not_ids_are_ignored() {
        assertNull(Push.parse(mapOf("t" to "call", "id" to c)))
        assertNull(Push.parse(mapOf("t" to "owner", "kind" to "other")))
        assertNull(Push.parse(mapOf("t" to "message", "c" to "$c&owner=join", "m" to m)))
        assertNull(Push.parse(mapOf("t" to "message", "c" to c)))
        assertNull(Push.parse(emptyMap()))
    }

    @Test fun a_notice_needs_every_part_and_a_path_on_gom() {
        assertEquals(Notice("Mẹ", "chào", "/c/$c", c), Notice.parse("""{"title":"Mẹ","body":"chào","url":"/c/$c","tag":"$c"}"""))
        assertNull(Notice.parse("""{"title":"Mẹ","body":"chào","url":"https://evil.example/","tag":"$c"}"""))
        assertNull(Notice.parse("""{"title":"","body":"chào","url":"/","tag":"x"}"""))
        assertNull(Notice.parse("""<html>login</html>"""))
    }
}

class KeyboardImageTest {
    @Test fun only_the_kinds_a_message_takes_go_to_the_page() {
        val gif = JSONObject(ToPage.image("image/gif", "GIF89a".toByteArray())!!)
        assertEquals("image", gif.getString("t"))
        assertEquals("image/gif", gif.getString("type"))
        assertEquals("GIF89a", String(java.util.Base64.getDecoder().decode(gif.getString("data"))))
        assertNull(ToPage.image("text/html", "<p>".toByteArray()))
        assertNull(ToPage.image("image/svg+xml", "<svg/>".toByteArray()))
    }

    @Test fun a_picture_over_the_servers_limit_is_not_read_whole() {
        assertEquals(4, readAtMost(java.io.ByteArrayInputStream(ByteArray(4)), 4)!!.size)
        assertNull(readAtMost(java.io.ByteArrayInputStream(ByteArray(5)), 4))
        assertNull(readAtMost(java.io.ByteArrayInputStream(ByteArray(200_000)), 100_000))
        assertEquals(0, readAtMost(java.io.ByteArrayInputStream(ByteArray(0)), 0)!!.size)
    }
}
