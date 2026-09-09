# DTDD icon — animated WebP build

Source frames: 36 PNGs, 800x450, 12 fps, 3 s loop, ending on the green (cleared) frame.
poster-static.png is the final frame, for a static fallback.

## Encode (lossy WebP, q75, plays 3 times then holds)

    img2webp -loop 3 -lossy -q 75 -d 83 frame-*.png -o dtdd-icon.webp

-d 83 is the per-frame delay in ms (1000 / 12). `-loop 3` plays three times
then rests on the last frame, which is the green resolved state.

Check the result:

    ls -l dtdd-icon.webp     # target: under 150 KB
    webpinfo dtdd-icon.webp

If it lands over 150 KB, drop quality to 65 or the frame rate to 10 fps
(re-render at 30 frames, -d 100).

## ffmpeg alternative

    ffmpeg -framerate 12 -i frame-%02d.png -c:v libwebp -lossless 0 -q:v 75 \
      -loop 3 -preset picture -an dtdd-icon.webp
