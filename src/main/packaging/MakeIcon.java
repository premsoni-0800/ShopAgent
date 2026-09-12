import java.awt.*;
import java.awt.geom.*;
import java.awt.image.BufferedImage;
import java.io.*;
import javax.imageio.ImageIO;

/**
 * Draws the Printly mark and writes it as a Windows .ico.
 *
 * The brand mark is the printer glyph on the purple gradient tile, the same
 * one the sidebar and the customer site use. Drawn in Java2D rather than
 * converted from the SVG because nothing on this machine rasterises SVG, and
 * a build that needs ImageMagick installed is a build that breaks on the next
 * machine.
 *
 * The .ico carries PNG-compressed entries, which Windows has accepted since
 * Vista and jpackage passes straight through - so one file covers every size
 * the shell asks for, from the 16px tray to the 256px properties dialog.
 */
public class MakeIcon {

    static final int[] SIZES = { 16, 24, 32, 48, 64, 128, 256 };

    public static void main(String[] args) throws Exception {
        File out = new File(args[0]);
        out.getParentFile().mkdirs();

        byte[][] pngs = new byte[SIZES.length][];
        for (int i = 0; i < SIZES.length; i++) {
            ByteArrayOutputStream b = new ByteArrayOutputStream();
            ImageIO.write(mark(SIZES[i]), "png", b);
            pngs[i] = b.toByteArray();
        }

        try (DataOutputStream d = new DataOutputStream(new BufferedOutputStream(new FileOutputStream(out)))) {
            d.writeShort(0);                      // reserved
            d.writeShort(Short.reverseBytes((short) 1));  // type 1 = icon
            d.writeShort(Short.reverseBytes((short) SIZES.length));

            int offset = 6 + 16 * SIZES.length;
            for (int i = 0; i < SIZES.length; i++) {
                int s = SIZES[i];
                d.writeByte(s >= 256 ? 0 : s);    // width  (0 means 256)
                d.writeByte(s >= 256 ? 0 : s);    // height
                d.writeByte(0);                   // palette
                d.writeByte(0);                   // reserved
                d.writeShort(Short.reverseBytes((short) 1));   // colour planes
                d.writeShort(Short.reverseBytes((short) 32));  // bits per pixel
                d.writeInt(Integer.reverseBytes(pngs[i].length));
                d.writeInt(Integer.reverseBytes(offset));
                offset += pngs[i].length;
            }
            for (byte[] png : pngs) d.write(png);
        }
        if (args.length > 1) {
            File png = new File(args[1]);
            png.getParentFile().mkdirs();
            ImageIO.write(mark(256), "png", png);
            System.out.println("wrote " + png.getAbsolutePath() + " (" + png.length() + " bytes)");
        }

        System.out.println("wrote " + out.getAbsolutePath() + " (" + out.length() + " bytes, " + SIZES.length + " sizes)");
    }

    /** The rounded gradient tile with the printer glyph, at [size] px. */
    static BufferedImage mark(int size) {
        BufferedImage img = new BufferedImage(size, size, BufferedImage.TYPE_INT_ARGB);
        Graphics2D g = img.createGraphics();
        g.setRenderingHint(RenderingHints.KEY_ANTIALIASING, RenderingHints.VALUE_ANTIALIAS_ON);
        g.setRenderingHint(RenderingHints.KEY_STROKE_CONTROL, RenderingHints.VALUE_STROKE_PURE);
        g.setRenderingHint(RenderingHints.KEY_RENDERING, RenderingHints.VALUE_RENDER_QUALITY);

        // The brand gradient, top-left to bottom-right, same stops as the CSS.
        g.setPaint(new GradientPaint(0, 0, new Color(0xAB9BFA), size, size, new Color(0x5E3FD9)));
        double r = size * 0.30;
        g.fill(new RoundRectangle2D.Double(0, 0, size, size, r, r));

        // Printer glyph, laid out on a 0..1 grid so it scales exactly.
        g.setColor(Color.WHITE);
        double u = size;
        // paper feed
        double feedW = 0.44 * u, feedH = 0.15 * u;
        g.fill(new RoundRectangle2D.Double((u - feedW) / 2, 0.16 * u, feedW, feedH, 0.05 * u, 0.05 * u));
        // body
        double bodyW = 0.70 * u, bodyH = 0.32 * u;
        g.fill(new RoundRectangle2D.Double((u - bodyW) / 2, 0.34 * u, bodyW, bodyH, 0.09 * u, 0.09 * u));
        // output tray
        double trayW = 0.46 * u, trayH = 0.20 * u;
        g.fill(new RoundRectangle2D.Double((u - trayW) / 2, 0.62 * u, trayW, trayH, 0.05 * u, 0.05 * u));

        // The two cutouts, punched rather than painted: a dot filled in the
        // body colour disappears on a gradient, which is the bug every
        // from-scratch version of this icon has had.
        g.setComposite(AlphaComposite.Clear);
        double slotW = 0.34 * u, slotH = 0.10 * u;
        g.fill(new RoundRectangle2D.Double((u - slotW) / 2, 0.575 * u, slotW, slotH, 0.03 * u, 0.03 * u));
        if (size >= 32) {
            double lamp = 0.055 * u;
            g.fill(new Ellipse2D.Double(0.70 * u, 0.42 * u, lamp, lamp));
        }

        g.dispose();
        return img;
    }
}
