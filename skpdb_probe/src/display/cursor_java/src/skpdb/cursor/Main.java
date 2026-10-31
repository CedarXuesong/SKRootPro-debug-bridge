package skpdb.cursor;

import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.PorterDuff;
import android.graphics.Typeface;
import android.os.Build;
import android.view.Surface;
import android.view.SurfaceControl;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.lang.reflect.Method;

/** Root CPU overlays: no input channels, trusted, and excluded from screenshots. */
public final class Main {
    private static Method hidden(String name, Class<?>... args) throws Exception {
        Method method = SurfaceControl.Transaction.class.getDeclaredMethod(name, args);
        method.setAccessible(true); return method;
    }
    private static final Method SHOW, HIDE, MATRIX, REMOVE;
    static {
        try {
            SHOW = hidden("show", SurfaceControl.class); HIDE = hidden("hide", SurfaceControl.class);
            MATRIX = hidden("setMatrix", SurfaceControl.class, float.class, float.class, float.class, float.class);
            REMOVE = hidden("remove", SurfaceControl.class);
        } catch (Exception error) { throw new ExceptionInInitializerError(error); }
    }
    private static final long FRAME_NANOS = 4_000_000L;
    private static final Object STATE = new Object();
    private static int targetX, targetY;
    private static boolean targetVisible, running = true;
    private static HudState targetHud;
    private static String stage = "initialize";
    private static Typeface panelTypeface;

    private static void initializeFonts() throws Exception {
        // app_process bypasses the normal app startup that supplies the lazy
        // system font map. Vendor Paint hooks may reject an uninitialized face.
        try {
            Method load = Typeface.class.getDeclaredMethod("loadPreinstalledSystemFontMap");
            load.setAccessible(true); load.invoke(null);
        } catch (NoSuchMethodException olderAndroid) {
            // Earlier Android releases initialize the map in Typeface's static initializer.
        }
        panelTypeface = Typeface.create("sans-serif", Typeface.NORMAL);
        Paint probe = new Paint();
        probe.setTypeface(panelTypeface); probe.measureText("SKPDB Canary");
    }

    private static void report(Throwable error) {
        String message = String.valueOf(error.getMessage()).replace('\n', ' ').replace('\r', ' ');
        if (message.length() > 150) message = message.substring(0, 150);
        System.out.println("ERROR " + stage + ": " + error.getClass().getSimpleName() + ": " + message);
        System.out.flush();
        error.printStackTrace(System.err);
    }

    private static final class Layer {
        final SurfaceControl control;
        final Surface surface;
        Layer(String name, int width, int height, int z) throws Exception {
            SurfaceControl.Builder builder = new SurfaceControl.Builder()
                    .setName(name).setFormat(PixelFormat.RGBA_8888)
                    .setBufferSize(width, height).setOpaque(false);
            Method flags = SurfaceControl.Builder.class.getDeclaredMethod("setFlags", int.class);
            flags.setAccessible(true);
            flags.invoke(builder, 0x40); // eSkipScreenshot
            control = builder.build();
            try {
                try (SurfaceControl.Transaction t = new SurfaceControl.Transaction()) {
                    // Untrusted overlays above games suppress touches even without an input channel.
                    if (Build.VERSION.SDK_INT >= 31) {
                        Method trusted = SurfaceControl.Transaction.class.getDeclaredMethod(
                                "setTrustedOverlay", SurfaceControl.class, boolean.class);
                        trusted.setAccessible(true); trusted.invoke(t, control, true);
                    }
                    t.setLayer(control, z).setAlpha(control, 0);
                    HIDE.invoke(t, control); t.apply();
                }
                surface = new Surface(control);
            } catch (Exception error) { control.release(); throw error; }
        }
        Canvas lock() throws Exception {
            for (int i = 0; i < 20; i++) {
                try { return surface.lockCanvas(null); }
                catch (Exception error) { if (i == 19) throw error; Thread.sleep(10); }
            }
            throw new IllegalStateException("No overlay canvas");
        }
        void release() {
            try (SurfaceControl.Transaction t = new SurfaceControl.Transaction()) { REMOVE.invoke(t, control); t.apply(); }
            catch (Exception error) { System.err.println("Overlay cleanup: " + error); }
            finally { surface.release(); control.release(); }
        }
    }

    private static void arrow(Layer layer) throws Exception {
        Canvas canvas = layer.lock();
        try {
            canvas.drawColor(Color.TRANSPARENT, PorterDuff.Mode.CLEAR);
            Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
            Path path = new Path();
            path.moveTo(2, 2); path.lineTo(2, 50); path.lineTo(15, 38);
            path.lineTo(24, 58); path.lineTo(32, 54); path.lineTo(23, 35); path.lineTo(42, 33); path.close();
            paint.setColor(Color.WHITE); canvas.drawPath(path, paint);
            paint.setColor(Color.BLACK); paint.setStyle(Paint.Style.STROKE); paint.setStrokeWidth(2);
            canvas.drawPath(path, paint);
        } finally { layer.surface.unlockCanvasAndPost(canvas); }
    }

    private static void panel(Layer layer, String[] lines, boolean canary) throws Exception {
        Canvas canvas = layer.lock();
        try {
            canvas.drawColor(Color.TRANSPARENT, PorterDuff.Mode.CLEAR);
            Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
            // Transparent panels retain only a small text shadow for contrast.
            paint.setShadowLayer(2, 0, 1, Color.BLACK);
            paint.setColor(canary ? 0xFFFFD183 : Color.WHITE);
            paint.setTextSize(canary ? 26 : 23);
            paint.setTypeface(panelTypeface);
            for (int i = 0; i < lines.length; i++) canvas.drawText(lines[i], 12, 29 + i * 29, paint);
        } finally { layer.surface.unlockCanvasAndPost(canvas); }
    }

    private static void place(SurfaceControl.Transaction t, Layer layer, float x, float y, float scale, boolean visible) throws Exception {
        t.setPosition(layer.control, x, y).setAlpha(layer.control, visible ? 1 : 0);
        MATRIX.invoke(t, layer.control, scale, 0f, 0f, scale);
        (visible ? SHOW : HIDE).invoke(t, layer.control);
    }

    private static void readCommands() {
        try {
            BufferedReader reader = new BufferedReader(new InputStreamReader(System.in));
            String line;
            while ((line = reader.readLine()) != null) {
                String[] p = line.trim().split("\\s+");
                synchronized (STATE) {
                    if (p[0].equals("move") && p.length == 3) {
                        targetX = Integer.parseInt(p[1]); targetY = Integer.parseInt(p[2]);
                    } else if (p[0].equals("visible") && p.length == 2) targetVisible = p[1].equals("1");
                    else if (p[0].equals("hud")) targetHud = new HudState(p, System.nanoTime());
                    else if (p[0].equals("destroy")) break;
                }
            }
        } catch (Throwable error) { report(error); }
        finally { synchronized (STATE) { running = false; } }
    }

    public static void main(String[] args) throws Exception {
        Layer cursor = null, debug = null, canary = null;
        try {
            if (BuildInfo.CANARY) {
                stage = "initialize fonts";
                initializeFonts();
            }
            stage = "create cursor";
            cursor = new Layer("SKPDB Cursor", 64, 64, Integer.MAX_VALUE - 1);
            if (BuildInfo.CANARY) {
                stage = "create debug";
                debug = new Layer("SKPDB Debug", 560, 158, Integer.MAX_VALUE - 2);
                stage = "create canary";
                canary = new Layer("SKPDB Canary", 300, 46, Integer.MAX_VALUE - 2);
                panel(canary, new String[] { "SKPDB · Canary 版" }, true);
            }
            stage = "draw initial buffers";
            arrow(cursor);
            System.out.println("READY"); System.out.flush();
            Thread input = new Thread(new Runnable() { public void run() { readCommands(); } }, "skpdb-overlay-input");
            input.setDaemon(true); input.start();
            int lastX = Integer.MIN_VALUE, lastY = Integer.MIN_VALUE;
            boolean lastVisible = false, lastHudVisible = false, haveTarget = false;
            double renderX = 0, renderY = 0;
            HudState rendered = null;
            long next = System.nanoTime();
            while (true) {
                stage = "render";
                int x, y; boolean visible; HudState hud;
                synchronized (STATE) {
                    if (!running) break;
                    x = targetX; y = targetY; visible = targetVisible; hud = targetHud;
                }
                if (!haveTarget || (!lastVisible && visible) || Math.abs(x - renderX) + Math.abs(y - renderY) > 240) {
                    renderX = x; renderY = y; haveTarget = true;
                } else {
                    renderX += (x - renderX) * .38; renderY += (y - renderY) * .38;
                    if (Math.abs(x - renderX) < .35 && Math.abs(y - renderY) < .35) { renderX = x; renderY = y; }
                }
                int drawX = (int)Math.round(renderX), drawY = (int)Math.round(renderY);
                boolean hudVisible = BuildInfo.CANARY && hud != null && hud.visible(System.nanoTime());
                boolean newHud = hud != null && hud != rendered;
                if (BuildInfo.CANARY && newHud && hudVisible) panel(debug, hud.lines(), false);
                if (drawX != lastX || drawY != lastY || visible != lastVisible || newHud || hudVisible != lastHudVisible) {
                    try (SurfaceControl.Transaction t = new SurfaceControl.Transaction()) {
                        place(t, cursor, drawX, drawY, 1, visible);
                        if (BuildInfo.CANARY && hud != null) {
                            float scale = hud.scale(), margin = 12 * scale;
                            place(t, debug, margin, margin, scale, hudVisible);
                            place(t, canary, margin, hud.bottomY(), scale, hudVisible);
                        }
                        t.apply();
                    }
                    if (newHud) { System.out.println("HUD " + hud.sequence); System.out.flush(); rendered = hud; }
                    lastX = drawX; lastY = drawY; lastVisible = visible; lastHudVisible = hudVisible;
                }
                next += FRAME_NANOS;
                long sleep = next - System.nanoTime();
                if (sleep > 0) Thread.sleep(sleep / 1_000_000L, (int)(sleep % 1_000_000L));
                else next = System.nanoTime();
            }
        } catch (Throwable error) {
            report(error);
            throw error;
        } finally {
            if (canary != null) canary.release();
            if (debug != null) debug.release();
            if (cursor != null) cursor.release();
        }
    }
}
