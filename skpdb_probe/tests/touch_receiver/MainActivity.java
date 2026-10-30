package com.skpdb.touchtest;
import android.app.Activity;
import android.os.Bundle;
import android.view.View;
import android.view.MotionEvent;
import android.graphics.Canvas;
import android.graphics.Paint;
import java.io.File;
import java.io.FileWriter;

/** Opt-in inert touch receiver: no buttons, permissions, network, or game launch. */
public final class MainActivity extends Activity {
    private FileWriter log;
    private int events;
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        getWindow().addFlags(128);
        try { log = new FileWriter(new File(getFilesDir(), "events.tsv"), false); }
        catch (Exception error) { throw new RuntimeException(error); }
        setContentView(new View(this) {
            private final Paint paint = new Paint(3);
            @Override public void onDraw(Canvas canvas) {
                canvas.drawColor(0xff15202b); paint.setColor(0xffeeeeee); paint.setTextSize(36);
                canvas.drawText("SKPDB Touch Regression", 40, 120, paint);
                canvas.drawText("Events: " + events, 40, 180, paint);
            }
            @Override public boolean onTouchEvent(MotionEvent event) {
                try {
                    log.write(event.getEventTime() + "\t" + event.getActionMasked() + "\t" +
                        event.getPointerCount() + "\t" + event.getDeviceId() + "\t" +
                        event.getX() + "\t" + event.getY() + "\n");
                    log.flush(); ++events; invalidate();
                } catch (Exception error) { throw new RuntimeException(error); }
                return true;
            }
        });
    }
    @Override public void onDestroy() {
        try { if (log != null) log.close(); } catch (Exception ignored) {}
        super.onDestroy();
    }
}
