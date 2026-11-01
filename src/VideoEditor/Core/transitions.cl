// transitions.json의 "expr"(FFmpeg xfade custom 수식)과 같은 그림을 그리는 xfade_opencl 커널.
// 커널 이름 = 효과 id. 정답은 transitions.json의 수식이며, 여기 식은 그 수식을 그대로 옮긴 것이다.
//
// 약속(FFmpeg xfade_opencl custom):
//   progress(P)는 1(시작) -> 0(끝). src1 = 앞 화면(A), src2 = 새 화면(B).
//   입력은 rgba, 0~1, 알파를 곱한(premultiplied) 색. RGBA 네 값을 같은 식으로 섞는다.
//   수식의 255는 1.0f, /255·-510 같은 상수도 0~1 값에 맞춰 바꿨다.
//   수식의 평면 a0/a1/a2/a3(gbrap: G/B/R/A)은 rgba의 .y/.z/.x/.w.
//
// 읽기(at): CPU xfade의 a0(x,y)는 좌표를 [0, W-1]로 자른 뒤 정수로 버림(가장 가까운 아래 픽셀)해서 읽는다.
// GPU도 똑같이 정수 좌표로 읽어야 CPU와 같은 픽셀이 나온다(보간하면 경계·확대 부분이 달라진다).
// 모양 판정 좌표도 수식과 같은 픽셀 좌표(X = p.x, Y = p.y)를 쓴다.

#define PI_F 3.14159265358979f

__constant sampler_t smp = CLK_NORMALIZED_COORDS_FALSE | CLK_ADDRESS_CLAMP_TO_EDGE | CLK_FILTER_NEAREST;

// CPU xfade getpix와 같은 읽기: 좌표를 가장자리로 자르고 정수로 버린다.
static float4 at(__read_only image2d_t img, float x, float y, float W, float H)
{
    int2 q = (int2)((int)clamp(x, 0.0f, W - 1.0f), (int)clamp(y, 0.0f, H - 1.0f));
    return read_imagef(img, smp, q);
}

// FFmpeg mod(a, b) = a - b*floor(a/b)
static float fmodf_floor(float a, float b) { return a - b * floor(a / b); }

// 수식에 자주 나오는 c*c*(3-2c), c = clip(v, 0, 1)
static float ss01(float v) { float c = clamp(v, 0.0f, 1.0f); return c * c * (3.0f - 2.0f * c); }

#define PROLOGUE                                                           \
    int2 p = (int2)(get_global_id(0), get_global_id(1));                   \
    float W = (float)get_image_width(dst), H = (float)get_image_height(dst); \
    float X = (float)p.x, Y = (float)p.y;                                  \
    float P = progress;

// ---------------- 밀어내기 ----------------

__kernel void SqueezeV(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float4 c;
    if (fabs(X - W / 2) < W / 2 * P)
        c = at(src1, W / 2 + (X - W / 2) / P, Y, W, H);
    else
        c = read_imagef(src2, smp, p);
    write_imagef(dst, p, c);
}

// ---------------- 왜곡 ----------------

__kernel void Swirl(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float u = X / W - 0.5f, v = Y / H - 0.5f;
    float d = hypot(u, v);
    float th = (1.0f - d) * (1.0f - d) * (1.0f - fabs(1.0f - 2.0f * P)) * 25.1327f;
    float s = sin(th), co = cos(th);
    float sx = W * (0.5f + u * co - v * s);
    float sy = H * (0.5f + u * s + v * co);
    float4 c = at(src1, sx, sy, W, H) * P + at(src2, sx, sy, W, H) * (1.0f - P);
    write_imagef(dst, p, c);
}

__kernel void DreamyZoom(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float cx = X - W / 2, cy = Y - H / 2;
    float4 c;
    if (P > 0.5f) {
        float ang = 0.20944f * (1.0f - P), sc = 1.4f - 0.4f * P;
        float s = sin(ang), co = cos(ang);
        c = at(src1, W / 2 + (cx * co - cy * s) / sc, H / 2 + (cx * s + cy * co) / sc, W, H);
        c.xyz += (2.0f - 2.0f * P) * c.w;   // 색 평면에만 알파만큼 밝힘(번쩍)
    } else {
        float ang = -0.20944f * P, sc = 1.0f + 0.4f * P;
        float s = sin(ang), co = cos(ang);
        c = at(src2, W / 2 + (cx * co - cy * s) / sc, H / 2 + (cx * s + cy * co) / sc, W, H);
        c.xyz += 2.0f * P * c.w;
    }
    write_imagef(dst, p, min(c, 1.0f));     // min(255, ...)
}

__kernel void ZoomInOut(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float fa = 1.0f - ss01(2.0f - 2.0f * P);
    float fb = 1.0f - ss01(2.0f * P);
    float g = ss01((0.6f - P) / 0.2f);
    float4 a = at(src1, W / 2 + (X - W / 2) * fa, H / 2 + (Y - H / 2) * fa, W, H);
    float4 b = at(src2, W / 2 + (X - W / 2) * fb, H / 2 + (Y - H / 2) * fb, W, H);
    write_imagef(dst, p, a * (1.0f - g) + b * g);
}

__kernel void Ripple(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float d = hypot(X / W - 0.5f, Y / H - 0.5f);
    float k = (sin((1.0f - P) * (d * 100.0f - 50.0f)) + 0.5f) / 30.0f * (1.0f - P);
    float4 a = at(src1, X + (X - W / 2) * k, Y + (Y - H / 2) * k, W, H);
    float g = ss01((0.8f - P) / 0.8f);
    write_imagef(dst, p, a * (1.0f - g) + read_imagef(src2, smp, p) * g);
}

__kernel void WaterDrop(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float d = hypot(X / W - 0.5f, Y / H - 0.5f);
    float4 a;
    if (d > 1.0f - P) {
        a = read_imagef(src1, smp, p);
    } else {
        float k = sin(30.0f * (d - 1.0f + P));
        a = at(src1, X + (X - W / 2) * k, Y + (Y - H / 2) * k, W, H);
    }
    write_imagef(dst, p, a * P + read_imagef(src2, smp, p) * (1.0f - P));
}

__kernel void Morph(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float4 a0 = read_imagef(src1, smp, p), b0 = read_imagef(src2, smp, p);
    // (a2+a1+b2+b1-510)/5100 -> (R+B 합 - 2)/20, (a0+a1+b0+b1-510)/5100 -> (G+B 합 - 2)/20
    float ox = (a0.x + a0.z + b0.x + b0.z - 2.0f) / 20.0f;
    float oy = (a0.y + a0.z + b0.y + b0.z - 2.0f) / 20.0f;
    float4 a = at(src1, X + ox * W * (1.0f - P), Y - oy * H * (1.0f - P), W, H);
    float4 b = at(src2, X - ox * W * P, Y + oy * H * P, W, H);
    write_imagef(dst, p, a * P + b * (1.0f - P));
}

// ---------------- 모양 ----------------

__kernel void Heart(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float s = H * (1.0f - P) * (1.0f - P) * 1.6f + 1.0f;
    float u = (X - W / 2) / s;
    float v = (H / 2 - Y) / s + 0.15f;
    float q = u * u + v * v - 1.0f;
    bool inside = q * q * q - u * u * v * v * v <= 0.0f;
    write_imagef(dst, p, inside ? read_imagef(src2, smp, p) : read_imagef(src1, smp, p));
}

__kernel void Flower(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float r = hypot(X - W / 2, Y - H / 2) / H;
    float lim = (1.0f - P) * (1.0f - P) * 2.4f * (0.55f + 0.45f * fabs(cos(2.5f * atan2(Y - H / 2, X - W / 2))));
    write_imagef(dst, p, r < lim ? read_imagef(src2, smp, p) : read_imagef(src1, smp, p));
}

__kernel void Diamond(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    bool inside = fabs(X / W - 0.5f) + fabs(Y / H - 0.5f) <= (1.0f - P) * 1.01f;
    write_imagef(dst, p, inside ? read_imagef(src2, smp, p) : read_imagef(src1, smp, p));
}

__kernel void Blinds(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float h = H / 8;
    bool inside = fmodf_floor(Y, h) / h < 1.0f - P;
    write_imagef(dst, p, inside ? read_imagef(src2, smp, p) : read_imagef(src1, smp, p));
}

__kernel void VerticalBlinds(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float w = W / 10;
    bool inside = fmodf_floor(X, w) / w < 1.0f - P;
    write_imagef(dst, p, inside ? read_imagef(src2, smp, p) : read_imagef(src1, smp, p));
}

__kernel void Checker(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float cw = W / 8, ch = H / 4.5f;
    float parity = fmodf_floor(floor(X / cw) + floor(Y / ch), 2.0f);
    bool inside = 2.0f * (1.0f - P) - parity > fmodf_floor(Y, ch) / ch;
    write_imagef(dst, p, inside ? read_imagef(src2, smp, p) : read_imagef(src1, smp, p));
}

__kernel void Pinwheel(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float t = atan2(Y / H - 0.5f, X / W - 0.5f) + 2.0f * (1.0f - P);
    float m = t - 0.785398f * floor(t / 0.785398f);
    bool keepA = 1.0f - P < m;
    write_imagef(dst, p, keepA ? read_imagef(src1, smp, p) : read_imagef(src2, smp, p));
}

__kernel void StarWipe(__write_only image2d_t dst, __read_only image2d_t src1, __read_only image2d_t src2, float progress)
{
    PROLOGUE
    float u = X / W - 0.5f, v = 0.5f - Y / H;
    float rx = u * 0.587785f - v * 0.809017f;
    float ry = u * 0.809017f + v * 0.587785f;
    float th = -1.256637f * (floor((atan2(ry, rx) + PI_F) / 1.256637f) + 0.5f);
    float s = sin(th), co = cos(th);
    float d = fabs(rx * s + ry * co);
    float lim = (1.02f * (1.0f - P) - 0.01f) + 0.3f * (rx * co - ry * s);
    float4 c;
    if (d < lim)              c = read_imagef(src2, smp, p);
    else if (d < lim + 0.01f) c = (float4)(1.0f);   // 흰 테(255)
    else                      c = read_imagef(src1, smp, p);
    write_imagef(dst, p, c);
}
