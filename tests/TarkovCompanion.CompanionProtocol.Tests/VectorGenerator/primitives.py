"""Independent pure-Python primitives for paired-protocol test vectors (no third-party packages)."""
import base64, hashlib, hmac, struct, uuid

# ---------------- encodings ----------------
def b64u(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).decode().rstrip("=")

def unb64u(text: str) -> bytes:
    return base64.urlsafe_b64decode(text + "=" * (-len(text) % 4))

def lp(data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + data

def utf8(text: str) -> bytes:
    return lp(text.encode("utf-8"))

def u16(v): return struct.pack(">H", v)
def u32(v): return struct.pack(">I", v)
def u64(v): return struct.pack(">Q", v)
def i64(v): return struct.pack(">q", v)
def uid(text): return uuid.UUID(text).bytes  # RFC 4122/9562 big-endian layout
def version(major, minor): return u16(major) + u16(minor)

# ---------------- HKDF (RFC 5869) ----------------
def hkdf(ikm, salt, info, length):
    prk = hmac.new(salt, ikm, hashlib.sha256).digest()
    out, block, counter = b"", b"", 1
    while len(out) < length:
        block = hmac.new(prk, block + info + bytes([counter]), hashlib.sha256).digest()
        out += block
        counter += 1
    return out[:length]

# ---------------- P-256 ----------------
P = 2**256 - 2**224 + 2**192 + 2**96 - 1
A = P - 3
B = 0x5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B
N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
G = (0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296,
     0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5)

def inv(x, m): return pow(x, m - 2, m)

def add(p1, p2):
    if p1 is None: return p2
    if p2 is None: return p1
    (x1, y1), (x2, y2) = p1, p2
    if x1 == x2 and (y1 + y2) % P == 0: return None
    if p1 == p2:
        lam = (3 * x1 * x1 + A) * inv(2 * y1, P) % P
    else:
        lam = (y2 - y1) * inv(x2 - x1, P) % P
    x3 = (lam * lam - x1 - x2) % P
    return (x3, (lam * (x1 - x3) - y1) % P)

def mul(k, point=G):
    result, addend = None, point
    while k:
        if k & 1: result = add(result, addend)
        addend = add(addend, addend)
        k >>= 1
    return result

def on_curve(point):
    x, y = point
    return (y * y - (x * x * x + A * x + B)) % P == 0

SPKI_PREFIX = bytes.fromhex("3059301306072a8648ce3d020106082a8648ce3d030107034200")
def spki(point):
    return SPKI_PREFIX + b"\x04" + point[0].to_bytes(32, "big") + point[1].to_bytes(32, "big")

def private_key(label):
    return int.from_bytes(hashlib.sha256(label.encode()).digest(), "big") % N

def ecdh(d, point):
    return mul(d, point)[0].to_bytes(32, "big")

def ecdsa_sign(d, message, label):
    e = int.from_bytes(hashlib.sha256(message).digest(), "big")
    counter = 0
    while True:
        k = int.from_bytes(hashlib.sha256(d.to_bytes(32, "big") + message + label.encode() + bytes([counter])).digest(), "big") % N
        counter += 1
        if k == 0: continue
        r = mul(k)[0] % N
        if r == 0: continue
        s = inv(k, N) * (e + r * d) % N
        if s == 0: continue
        return r, s

def ecdsa_verify(point, message, r, s):
    e = int.from_bytes(hashlib.sha256(message).digest(), "big")
    w = inv(s, N)
    xy = add(mul(e * w % N), mul(r * w % N, point))
    return xy is not None and xy[0] % N == r

def p1363(r, s): return r.to_bytes(32, "big") + s.to_bytes(32, "big")

def der(r, s):
    def integer(v):
        raw = v.to_bytes((v.bit_length() + 7) // 8 or 1, "big")
        if raw[0] & 0x80: raw = b"\x00" + raw
        return b"\x02" + bytes([len(raw)]) + raw
    body = integer(r) + integer(s)
    return b"\x30" + bytes([len(body)]) + body

# ---------------- AES-256 + GCM ----------------
SBOX = [0] * 256
def _init_sbox():
    p = q = 1
    SBOX[0] = 0x63
    while True:
        p = p ^ ((p << 1) & 0xFF) ^ (0x1B if p & 0x80 else 0)
        q ^= q << 1; q ^= q << 2; q ^= q << 4; q &= 0xFF
        if q & 0x80: q ^= 0x09
        x = q ^ (q << 1 | q >> 7) ^ (q << 2 | q >> 6) ^ (q << 3 | q >> 5) ^ (q << 4 | q >> 4)
        SBOX[p] = (x ^ 0x63) & 0xFF
        if p == 1: break
_init_sbox()

def _xtime(a): return ((a << 1) ^ 0x1B) & 0xFF if a & 0x80 else a << 1

def expand_key(key):
    nk, nr = len(key) // 4, len(key) // 4 + 6
    words = [list(key[i:i + 4]) for i in range(0, len(key), 4)]
    rcon = 1
    for i in range(nk, 4 * (nr + 1)):
        t = list(words[i - 1])
        if i % nk == 0:
            t = t[1:] + t[:1]
            t = [SBOX[b] for b in t]
            t[0] ^= rcon
            rcon = _xtime(rcon)
        elif nk > 6 and i % nk == 4:
            t = [SBOX[b] for b in t]
        words.append([words[i - nk][j] ^ t[j] for j in range(4)])
    return [[b for c in range(4) for b in words[r * 4 + c]] for r in range(nr + 1)]

def encrypt_block(round_keys, block):
    state = [b ^ k for b, k in zip(block, round_keys[0])]
    nr = len(round_keys) - 1
    for rnd in range(1, nr + 1):
        state = [SBOX[b] for b in state]
        # ShiftRows (column-major state: index = row + 4*col)
        state = [state[(r + 4 * ((c + r) % 4))] for c in range(4) for r in range(4)]
        if rnd != nr:
            mixed = []
            for c in range(4):
                a = state[4 * c:4 * c + 4]
                t = a[0] ^ a[1] ^ a[2] ^ a[3]
                mixed += [a[i] ^ t ^ _xtime(a[i] ^ a[(i + 1) % 4]) for i in range(4)]
            state = mixed
        state = [b ^ k for b, k in zip(state, round_keys[rnd])]
    return bytes(state)

def _gf_mult(x, y):
    R = 0xE1000000000000000000000000000000
    z, v = 0, y
    for i in range(127, -1, -1):
        if (x >> i) & 1: z ^= v
        v = (v >> 1) ^ R if v & 1 else v >> 1
    return z

def _ghash(h, aad, ciphertext):
    def blocks(data):
        data += b"\x00" * (-len(data) % 16)
        return [int.from_bytes(data[i:i + 16], "big") for i in range(0, len(data), 16)]
    y = 0
    for block in blocks(aad) + blocks(ciphertext) + [int.from_bytes(struct.pack(">QQ", len(aad) * 8, len(ciphertext) * 8), "big")]:
        y = _gf_mult(y ^ block, h)
    return y.to_bytes(16, "big")

def aes_gcm_encrypt(key, nonce, plaintext, aad):
    assert len(nonce) == 12
    rk = expand_key(key)
    h = int.from_bytes(encrypt_block(rk, b"\x00" * 16), "big")
    j0 = nonce + b"\x00\x00\x00\x01"
    counter = int.from_bytes(j0, "big")
    out = bytearray()
    for i in range(0, len(plaintext), 16):
        counter = (counter & ~0xFFFFFFFF) | ((counter + 1) & 0xFFFFFFFF)
        stream = encrypt_block(rk, counter.to_bytes(16, "big"))
        chunk = plaintext[i:i + 16]
        out += bytes(a ^ b for a, b in zip(chunk, stream))
    ciphertext = bytes(out)
    tag = bytes(a ^ b for a, b in zip(encrypt_block(rk, j0), _ghash(h, aad, ciphertext)))
    return ciphertext, tag

def self_test():
    # RFC 5869 test case 1
    assert hkdf(bytes([0x0b] * 22), bytes(range(13)), bytes(range(0xf0, 0xfa)), 42).hex() == \
        "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
    # P-256 group checks
    assert on_curve(G) and mul(N) is None
    a, b = private_key("self-test-a"), private_key("self-test-b")
    assert ecdh(a, mul(b)) == ecdh(b, mul(a))
    r, s = ecdsa_sign(a, b"message", "t")
    assert ecdsa_verify(mul(a), b"message", r, s) and not ecdsa_verify(mul(a), b"messagf", r, s)
    # FIPS-197 AES-256 appendix C.3
    assert encrypt_block(expand_key(bytes(range(32))), bytes.fromhex("00112233445566778899aabbccddeeff")).hex() == \
        "8ea2b7ca516745bfeafc49904b496089"
    # GCM spec test cases 13, 14, 16 (AES-256)
    c, t = aes_gcm_encrypt(bytes(32), bytes(12), b"", b"")
    assert t.hex() == "530f8afbc74536b9a963b4f1c4cb738b", t.hex()
    c, t = aes_gcm_encrypt(bytes(32), bytes(12), bytes(16), b"")
    assert c.hex() == "cea7403d4d606b6e074ec5d3baf39d18" and t.hex() == "d0d1c8a799996bf0265b98b5d48ab919", (c.hex(), t.hex())
    key = bytes.fromhex("feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308")
    pt = bytes.fromhex("d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b39")
    c, t = aes_gcm_encrypt(key, bytes.fromhex("cafebabefacedbaddecaf888"), pt, bytes.fromhex("feedfacedeadbeeffeedfacedeadbeefabaddad2"))
    assert c.hex() == "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838c5f61e6393ba7a0abcc9f662", c.hex()
    assert t.hex() == "76fc6ece0f4e1768cddf8853bb2d551b", t.hex()
    return True

if __name__ == "__main__":
    print("self-test", self_test())
