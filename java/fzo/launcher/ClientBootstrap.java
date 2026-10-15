package fzo.launcher;

import java.awt.Image;
import java.awt.Window;
import java.awt.event.ActionEvent;
import java.awt.event.ActionListener;
import java.io.InputStream;
import java.lang.reflect.InvocationTargetException;
import java.util.Collections;
import javax.imageio.ImageIO;
import javax.swing.Timer;

/** Original launcher helper, not a Minecraft modification or bytecode transformer. */
public final class ClientBootstrap {
    private ClientBootstrap() { }

    public static void main(final String[] args) throws Throwable {
        Timer timer = null;
        try {
            final Image icon;
            InputStream input = ClientBootstrap.class.getResourceAsStream("/neuro-cube.png");
            if (input == null) throw new IllegalStateException("Missing game icon");
            try { icon = ImageIO.read(input); } finally { input.close(); }
            if (icon == null) throw new IllegalStateException("Invalid game icon");
            timer = new Timer(1000, new ActionListener() {
                public void actionPerformed(ActionEvent event) {
                    // AWT events run on the event dispatch thread. Recheck new windows
                    // and icons reset by the game without repeatedly allocating icons.
                    for (Window window : Window.getWindows()) {
                        if (window.isDisplayable() && !window.getIconImages().contains(icon)) {
                            window.setIconImages(Collections.singletonList(icon));
                        }
                    }
                }
            });
            timer.start();
        } catch (Exception ex) {
            // Cosmetic failure must never block Minecraft startup.
            System.err.println("FZO game icon unavailable: " + ex.toString());
        }
        try {
            Class.forName("net.minecraft.client.Minecraft").getMethod("main", String[].class)
                .invoke(null, new Object[] { args });
        } catch (InvocationTargetException ex) {
            if (timer != null) timer.stop();
            throw ex.getCause();
        } catch (Throwable ex) {
            if (timer != null) timer.stop();
            throw ex;
        }
    }
}
