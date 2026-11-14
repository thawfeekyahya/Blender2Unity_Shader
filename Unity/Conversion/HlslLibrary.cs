namespace B2U
{
    /// <summary>
    /// Shared HLSL helpers ported from Blender's GPU material GLSL. Emitted once into every
    /// generated .shader, and into the per-material .hlsl include used by Shader Graph.
    /// </summary>
    public static class HlslLibrary
    {
        public const string Common = @"#ifndef B2U_COMMON_INCLUDED
#define B2U_COMMON_INCLUDED

#define B2U_PI 3.14159265359

float b2u_safe_div(float a, float b) { return b != 0.0 ? a / b : 0.0; }
float3 b2u_safe_div3(float3 a, float3 b) { return float3(b2u_safe_div(a.x, b.x), b2u_safe_div(a.y, b.y), b2u_safe_div(a.z, b.z)); }
float b2u_safe_mod(float a, float b) { return b != 0.0 ? fmod(a, b) : 0.0; }
float b2u_floored_mod(float a, float b) { return b != 0.0 ? a - floor(a / b) * b : 0.0; }
float b2u_safe_pow(float a, float b) { return (a < 0.0 && b != floor(b)) ? 0.0 : pow(abs(a), b) * ((a < 0.0 && fmod(abs(b), 2.0) == 1.0) ? -1.0 : 1.0); }
float b2u_safe_sqrt(float a) { return a > 0.0 ? sqrt(a) : 0.0; }
float b2u_safe_log(float a, float b) { return (a > 0.0 && b > 0.0 && b != 1.0) ? log(a) / log(b) : 0.0; }
float b2u_wrap(float v, float mx, float mn) { float r = mx - mn; return r != 0.0 ? v - r * floor((v - mn) / r) : mn; }
float b2u_pingpong(float a, float b) { return b != 0.0 ? abs(frac((a - b) / (b * 2.0)) * b * 2.0 - b) : 0.0; }
float b2u_smoothmin(float a, float b, float c) { if (c != 0.0) { float h = max(c - abs(a - b), 0.0) / c; return min(a, b) - h * h * h * c * (1.0 / 6.0); } return min(a, b); }
float b2u_luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

float3 b2u_rgb_to_hsv(float3 rgb)
{
    float cmax = max(rgb.r, max(rgb.g, rgb.b));
    float cmin = min(rgb.r, min(rgb.g, rgb.b));
    float cdelta = cmax - cmin;
    float h = 0.0, s = 0.0, v = cmax;
    if (cmax != 0.0) s = cdelta / cmax;
    if (s != 0.0)
    {
        float3 c = (float3(cmax, cmax, cmax) - rgb) / cdelta;
        if (rgb.r == cmax) h = c.b - c.g;
        else if (rgb.g == cmax) h = 2.0 + c.r - c.b;
        else h = 4.0 + c.g - c.r;
        h /= 6.0;
        if (h < 0.0) h += 1.0;
    }
    return float3(h, s, v);
}

float3 b2u_hsv_to_rgb(float3 hsv)
{
    float h = hsv.x, s = hsv.y, v = hsv.z;
    if (s == 0.0) return float3(v, v, v);
    if (h == 1.0) h = 0.0;
    h *= 6.0;
    float i = floor(h);
    float f = h - i;
    float p = v * (1.0 - s);
    float q = v * (1.0 - (s * f));
    float t = v * (1.0 - (s * (1.0 - f)));
    if (i == 0.0) return float3(v, t, p);
    if (i == 1.0) return float3(q, v, p);
    if (i == 2.0) return float3(p, v, t);
    if (i == 3.0) return float3(p, q, v);
    if (i == 4.0) return float3(t, p, v);
    return float3(v, p, q);
}

float3 b2u_rgb_to_hsl(float3 rgb)
{
    float cmax = max(rgb.r, max(rgb.g, rgb.b));
    float cmin = min(rgb.r, min(rgb.g, rgb.b));
    float h = 0.0, s = 0.0, l = (cmax + cmin) * 0.5;
    if (cmax != cmin)
    {
        float d = cmax - cmin;
        s = l > 0.5 ? d / (2.0 - cmax - cmin) : d / (cmax + cmin);
        if (cmax == rgb.r) h = (rgb.g - rgb.b) / d + (rgb.g < rgb.b ? 6.0 : 0.0);
        else if (cmax == rgb.g) h = (rgb.b - rgb.r) / d + 2.0;
        else h = (rgb.r - rgb.g) / d + 4.0;
    }
    return float3(h / 6.0, s, l);
}

float b2u_hue_to_rgb(float p, float q, float t)
{
    if (t < 0.0) t += 1.0;
    if (t > 1.0) t -= 1.0;
    if (t < 1.0 / 6.0) return p + (q - p) * 6.0 * t;
    if (t < 0.5) return q;
    if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6.0;
    return p;
}

float3 b2u_hsl_to_rgb(float3 hsl)
{
    if (hsl.y == 0.0) return hsl.zzz;
    float q = hsl.z < 0.5 ? hsl.z * (1.0 + hsl.y) : hsl.z + hsl.y - hsl.z * hsl.y;
    float p = 2.0 * hsl.z - q;
    return float3(b2u_hue_to_rgb(p, q, hsl.x + 1.0 / 3.0), b2u_hue_to_rgb(p, q, hsl.x), b2u_hue_to_rgb(p, q, hsl.x - 1.0 / 3.0));
}

// Blender color blend modes (gpu_shader_material_mix_color.glsl).
float3 b2u_blend(int mode, float f, float3 a, float3 b)
{
    float fm = 1.0 - f;
    float3 o = a;
    if (mode == 0) o = lerp(a, b, f);                                        // MIX
    else if (mode == 1) o = lerp(a, min(a, b), f);                            // DARKEN
    else if (mode == 2) o = lerp(a, a * b, f);                                // MULTIPLY
    else if (mode == 3)                                                       // BURN
    {
        [unroll] for (int k = 0; k < 3; k++)
        {
            float t = fm + f * b[k];
            o[k] = t <= 0.0 ? 0.0 : saturate(1.0 - (1.0 - a[k]) / t);
        }
    }
    else if (mode == 4) o = lerp(a, max(a, b), f);                            // LIGHTEN
    else if (mode == 5) o = 1.0 - (fm + f * (1.0 - b)) * (1.0 - a);           // SCREEN
    else if (mode == 6)                                                       // DODGE
    {
        [unroll] for (int k = 0; k < 3; k++)
        {
            if (a[k] != 0.0)
            {
                float t = 1.0 - f * b[k];
                o[k] = t <= 0.0 ? 1.0 : min(a[k] / t, 1.0);
            }
        }
    }
    else if (mode == 7) o = lerp(a, a + b, f);                                // ADD
    else if (mode == 8)                                                       // OVERLAY
    {
        [unroll] for (int k = 0; k < 3; k++)
            o[k] = a[k] < 0.5 ? a[k] * (fm + 2.0 * f * b[k]) : 1.0 - (fm + 2.0 * f * (1.0 - b[k])) * (1.0 - a[k]);
    }
    else if (mode == 9)                                                       // SOFT_LIGHT
    {
        float3 scr = 1.0 - (1.0 - b) * (1.0 - a);
        o = fm * a + f * ((1.0 - a) * b * a + a * scr);
    }
    else if (mode == 10) o = a + f * (2.0 * b - 1.0);                         // LINEAR_LIGHT
    else if (mode == 11) o = lerp(a, abs(a - b), f);                          // DIFFERENCE
    else if (mode == 12) o = max(lerp(a, a + b - 2.0 * a * b, f), 0.0);       // EXCLUSION
    else if (mode == 13) o = lerp(a, a - b, f);                               // SUBTRACT
    else if (mode == 14)                                                      // DIVIDE
    {
        [unroll] for (int k = 0; k < 3; k++)
            if (b[k] != 0.0) o[k] = fm * a[k] + f * a[k] / b[k];
    }
    else if (mode == 15)                                                      // HUE
    {
        float3 hb = b2u_rgb_to_hsv(b);
        if (hb.y != 0.0) { float3 ha = b2u_rgb_to_hsv(a); ha.x = hb.x; o = lerp(a, b2u_hsv_to_rgb(ha), f); }
    }
    else if (mode == 16)                                                      // SATURATION
    {
        float3 ha = b2u_rgb_to_hsv(a);
        if (ha.y != 0.0) { float3 hb = b2u_rgb_to_hsv(b); ha.y = fm * ha.y + f * hb.y; o = b2u_hsv_to_rgb(ha); }
    }
    else if (mode == 17)                                                      // VALUE
    {
        float3 ha = b2u_rgb_to_hsv(a); float3 hb = b2u_rgb_to_hsv(b);
        ha.z = fm * ha.z + f * hb.z; o = b2u_hsv_to_rgb(ha);
    }
    else if (mode == 18)                                                      // COLOR
    {
        float3 hb = b2u_rgb_to_hsv(b);
        if (hb.y != 0.0) { float3 ha = b2u_rgb_to_hsv(a); ha.x = hb.x; ha.y = hb.y; o = lerp(a, b2u_hsv_to_rgb(ha), f); }
    }
    return o;
}

// Linear interpolation into a 64 entry look-up table sampled in Blender (ramps, curves).
#define B2U_LUT_SIZE 64
#define B2U_LUT_SAMPLE(lut, t, outv) { float _x = saturate(t) * (B2U_LUT_SIZE - 1); int _i = (int)floor(_x); int _j = min(_i + 1, B2U_LUT_SIZE - 1); outv = lerp(lut[_i], lut[_j], _x - _i); }
#define B2U_LUT_NEAREST(lut, t, outv) { int _i = (int)clamp(floor(saturate(t) * (B2U_LUT_SIZE - 1) + 0.5), 0, B2U_LUT_SIZE - 1); outv = lut[_i]; }

float3 b2u_rotate_euler(float3 v, float3 r, bool inverse)
{
    float cx = cos(r.x), sx = sin(r.x), cy = cos(r.y), sy = sin(r.y), cz = cos(r.z), sz = sin(r.z);
    float3x3 m = float3x3(
        cy * cz, sy * sx * cz - cx * sz, sy * cx * cz + sx * sz,
        cy * sz, sy * sx * sz + cx * cz, sy * cx * sz - sx * cz,
        -sy,     cy * sx,                cy * cx);
    return inverse ? mul(transpose(m), v) : mul(m, v);
}

// Converts between Unity world space (Y up, left handed) and Blender space (Z up, right handed).
float3 b2u_to_blender(float3 u) { return float3(-u.x, -u.z, u.y); }
float3 b2u_from_blender(float3 b) { return float3(-b.x, b.z, -b.y); }

#endif // B2U_COMMON_INCLUDED
";
    }
}
