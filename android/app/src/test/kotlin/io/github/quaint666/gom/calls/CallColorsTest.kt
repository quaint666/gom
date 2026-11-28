package io.github.quaint666.gom.calls

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Test

/** The call screens' colours are the page's: each `call_` colour is gom.css's token of that name, light and dark. */
class CallColorsTest {
    /** The page's stylesheet: its parts in `Styles/`, in name order, as `build.mjs` joins them (the built `gom.css` is not in git). */
    private val css = File("../../src/Gom.Web/Styles").listFiles { f -> f.name.endsWith(".css") }!!.sortedBy { it.name }
        .joinToString("") { it.readText() }.let { it.substring(it.indexOf(":root")) }

    private fun colours(folder: String): Map<String, String> = Regex("""<color name="([a-z_]+)">(#[0-9A-Fa-f]{6})</color>""")
        .findAll(File("src/main/res/$folder/colors.xml").readText()).associate { it.groupValues[1] to it.groupValues[2].uppercase() }

    /** A token's two colours: `--name: light-dark(#light, #dark)` (the dark one may sit in a `var(--black-…, #dark)`), or one for both. */
    private fun token(name: String): Pair<String, String> {
        Regex("""--$name:\s*light-dark\((#[0-9A-Fa-f]{6}),\s*(?:var\(--[a-z-]+,\s*)?(#[0-9A-Fa-f]{6})""").find(css)?.let {
            return it.groupValues[1].uppercase() to it.groupValues[2].uppercase()
        }
        val one = Regex("""--$name:\s*(#[0-9A-Fa-f]{6})""").find(css) ?: error("--$name is not in the stylesheet")
        return one.groupValues[1].uppercase() to one.groupValues[1].uppercase()
    }

    @Test
    fun each_call_colour_is_the_pages_token() {
        val light = colours("values")
        val dark = colours("values-night")
        val names = light.keys.filter { it.startsWith("call_") }
        assertEquals(10, names.size)
        for (name in names) {
            val (day, night) = token(name.removePrefix("call_").replace('_', '-'))
            assertEquals(name, day, light[name])
            assertEquals(name, night, dark[name] ?: light[name])
        }
        assertEquals(token("bg"), light["page"] to dark["page"])
        assertEquals(token("btn").first, light["gom"])
    }
}
