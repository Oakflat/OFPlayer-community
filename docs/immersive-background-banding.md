# Immersive Background Banding Research

## Summary

The immersive player background banding issue is a perceptual rendering defect,
not just a CSS tuning problem.

The visible bands were caused by the combination of:

- Large, low-texture, dark gradients.
- 8-bit quantization in the browser, compositor, OS, GPU, or display chain.
- CSS gradients and `backdrop-filter` generating smooth intermediate surfaces
  without guaranteed dithering.
- Human visual sensitivity to low-frequency false contours.

Adding more CSS gradient stops helped only marginally. The qualitative
improvement came from moving the main immersive background to a WebGL renderer
that computes the gradient in shader space and applies per-pixel dithering
before final output quantization.

## User Impact

Observed before the WebGL background:

- Smooth dark cyan/green regions showed obvious contour bands.
- The bands became more visible in full-screen immersive playback because the
  background covered most of the viewport.
- Extra CSS blur and large radial gradients could make the issue worse by
  creating even smoother low-frequency surfaces.

Observed after the WebGL background:

- The main immersive background is visually much smoother.
- Remaining mild banding is mostly localized to glass surfaces, especially the
  footer, where `backdrop-filter` re-blurs the already dithered background.
- A pseudo-element footer glass experiment was tested, but the bottom
  progress/control area was intentionally rolled back to the original direct
  `backdrop-filter` treatment to preserve its prior visual character.

## Perceptual Model

Banding is most visible in smooth regions with little natural texture. Research
on chromatic banding describes the problem directly: when bit depth is
insufficient, smooth gradients become wide discrete bands, especially in
low-texture regions such as sky, water, skin, or similar flat background fields.

The human visual system does not simply average color values. It is sensitive to
contrast at certain spatial frequencies, so small quantization steps can become
visible as false contours. This is why a numerically small RGB change can look
like a visible ring or stripe when it spans a large smooth area.

In OFPlayer, the risky visual pattern is:

- Dark, low-contrast album-derived color fields.
- Full-window gradients.
- High blur radii.
- Long-lived static UI surfaces where the eye has time to inspect the contour.

## Browser And CSS Constraints

CSS gradients are not a reliable debanding primitive.

CSS Images Level 3 explicitly says user agents may dither gradient colors, but
that is permission, not a guarantee. In practice, browser and WebView output can
still show banding on smooth dark gradients.

CSS Color Level 4 and CSS Images Level 4 improve the color-interpolation model:
Oklab and Lab are designed for perceptually uniform gradients, and modern CSS
gradients can specify interpolation spaces. This helps color travel, but it does
not by itself solve final 8-bit output quantization or compositor banding.

`backdrop-filter` is also a special risk. A blur filter averages nearby pixels,
which can erase the high-frequency dither that was intentionally added to hide
quantization. If the blurred result is then stored or composited as another
smooth surface, banding can reappear.

## Implemented Strategy

The main immersive background is rendered by:

- `src/utils/immersiveBackgroundRenderer.js`
- `src/components/ImmersivePlayerView.vue`
- `src/utils/colorExtractor.js`

The renderer:

- Uses a full-viewport WebGL canvas behind immersive content.
- Sends album-derived colors to a fragment shader.
- Mixes colors in Oklab for perceptual smoothness.
- Uses linear-light blending for glow/shadow layering.
- Blends a low-opacity rare-accent glow so small but salient cover colors can
  participate in the immersive field.
- Adds low-amplitude triangular dither before writing the final pixel.
- Caps canvas backing resolution to avoid runaway memory on high-DPI displays.

The color extractor:

- Samples at `384 x 384` instead of `200 x 200`.
- Samples four corners plus center instead of only three regions.
- Scans the full cover for rare but salient accent colors, weighted by
  saturation, visible lightness, and hue distance from the dominant accent.
- Keeps a dense CSS gradient fallback for environments where WebGL is
  unavailable.

The footer glass currently remains the original direct `backdrop-filter` layer:

- `background: rgba(0, 0, 0, 0.22)`.
- `backdrop-filter: blur(40px) saturate(180%)`.
- No post-filter noise layer is applied to the bottom progress/control area.
- Further debanding work should avoid changing this area unless the design
  direction explicitly allows it.

## Guardrails

Do not treat immersive background banding as a normal CSS gradient issue.

Avoid:

- Solving by only adding more CSS gradient stops.
- Very large blurred CSS blobs over flat dark regions.
- Strong `backdrop-filter` over large surfaces without post-filter dithering.
- Transparent overlays that create fresh, smooth compositor surfaces after the
  dithered background has already been rendered.
- Heavy saturation boosts in glass layers.

Prefer:

- Perceptual color interpolation such as Oklab for gradient design.
- A separate rare-accent channel for small saturated colors that should not be
  averaged away by dominant cover colors.
- Linear-light blending for physical glow/shadow overlays.
- Dithering at the final render stage, before quantization.
- Noise or texture after any blur or glass effect that re-smooths the image.
- Mild glass blur radii with a stable tint layer.

## Tuning Notes

Current shader dither values are adaptive. Dark output colors receive less
noise, while mid/high luminance areas retain stronger debanding:

- Luma triangular noise: about `0.75 / 255` to `1.46 / 255`.
- Chroma triangular noise: about `0.11 / 255` to `0.22 / 255`.

If bands return on a specific display or GPU chain, tune in this order:

1. Adjust the luminance-to-dither curve before changing the whole shader.
2. Add or increase post-filter noise on the affected glass surface.
3. Reduce blur radius or saturation on the affected glass surface.
4. Reduce the size of large uniform regions by adding very soft structural
   variation in shader space.
5. If the background feels too monochrome, tune rare-accent salience thresholds
   or shader accent mask opacity before changing the whole palette.
6. Only after that, revisit color extraction or gradient stop density.

Avoid raising noise until it becomes visible as texture in normal listening. The
goal is to break quantization contours, not to create a grain aesthetic.

## Diagnostic Checks

When evaluating future regressions, capture the problem as specifically as
possible:

- Is the banding in the WebGL background or inside a glass/filter surface?
- Does it disappear if `backdrop-filter` is disabled?
- Does it change with Windows output color depth, HDR, or GPU driver settings?
- Does it appear in screenshots, or only when viewed on the physical display?
- Does it become worse after resizing, display scaling, or moving between
  monitors?

The screenshot distinction matters. If a band is visible in a screenshot, it was
likely produced before display scanout. If it is visible only on the physical
panel, the OS/GPU/display chain may be contributing through output bit depth,
panel FRC, color management, or display processing.

## Regression Smells

Treat these as likely regressions:

- Replacing the WebGL canvas with pure CSS gradients for the immersive
  background.
- Removing shader dithering.
- Moving noise to a layer that is later blurred or composited into a smooth
  surface.
- Changing footer or panel `backdrop-filter` behavior without checking the
  visual design impact on controls.
- Reintroducing large animated CSS blobs with high blur radii.
- Applying `filter: blur(...)` to the dithered canvas itself.

## Engineering Rule

For full-window immersive gradients, debanding must happen at the final visual
surface.

Perceptual interpolation chooses better colors, but dithering hides the last
quantization step. If another blur, glass, or compositor effect happens after
dithering, that later surface needs its own debanding treatment.

## References

- [A visual model for predicting chromatic banding artifacts](https://www.cl.cam.ac.uk/~rkm38/pdfs/denes2019banding_model.pdf)
- [CAMBI: Contrast-aware Multiscale Banding Index](https://arxiv.org/abs/2102.00079)
- [A Perceptual Visibility Metric for Banding Artifacts](https://research.google/pubs/a-perceptual-visibility-metric-for-banding-artifacts/)
- [Multi-scale probabilistic dithering for suppressing banding artifacts in digital images](https://projet.liris.cnrs.fr/imagine/pub/proceedings/ICIP-2007/pdfs/0400397.pdf)
- [CSS Images Module Level 3](https://www.w3.org/TR/css-images-3/)
- [CSS Images Module Level 4](https://www.w3.org/TR/css-images-4/)
- [CSS Color Module Level 4](https://www.w3.org/TR/css-color-4/)
- [Perceptually Dithered HDR for 8-Bit Interfaces](https://journal.smpte.org/periodicals/SMPTE%20Motion%20Imaging%20Journal/130/7/16/)
- [Scalar Spatiotemporal Blue Noise Masks](https://research.nvidia.com/publication/2021-12_scalar-spatiotemporal-blue-noise-masks)
