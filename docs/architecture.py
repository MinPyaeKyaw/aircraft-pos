"""Generates docs/architecture.png.

Kept in the repo so the diagram can be regenerated and reviewed as a diff
rather than being an opaque binary. Requires Pillow:

    pip install pillow
    python3 docs/architecture.py     # run from the repository root
"""
from PIL import Image, ImageDraw, ImageFont
import os, math

W, H, S = 1320, 900, 2
img = Image.new("RGB", (W*S, H*S), "#FFFFFF")
d = ImageDraw.Draw(img)

def font(size, bold=False):
    cands = (["/System/Library/Fonts/Supplemental/Arial Bold.ttf"] if bold
             else ["/System/Library/Fonts/Supplemental/Arial.ttf"]) + \
            ["/System/Library/Fonts/Helvetica.ttc"]
    for p in cands:
        if os.path.exists(p):
            try: return ImageFont.truetype(p, size*S)
            except Exception: pass
    return ImageFont.load_default()

F_TITLE, F_SUB = font(26, True), font(14)
F_BOX, F_BOXS  = font(15, True), font(12)
F_EDGE         = font(11)
INK, MUTED, LINE, READ = "#1A1D21", "#5C6670", "#77828E", "#B7C0C9"

C = {"client": ("#EEF1F4","#9AA5B1",INK), "api": ("#F3ECFF","#A166FF","#4B2E83"),
     "lambda": ("#FFF1E5","#ED7100","#8A4200"), "s3": ("#F0F7E6","#7AA116","#3F5A00"),
     "ddb": ("#EBF1FF","#527FFF","#1E3A8A"), "sqs": ("#FFEBF4","#E7157B","#8A0F49"),
     "dlq": ("#FCEBEB","#C7362F","#7A1F1B")}
B = {}

def box(k, x, y, w, h, kind, title, sub=None, tag=None):
    fill, edge, text = C[kind]
    d.rounded_rectangle([x*S, y*S, (x+w)*S, (y+h)*S], radius=8*S, fill=fill, outline=edge, width=2*S)
    if tag:
        d.ellipse([(x-12)*S,(y+h/2-12)*S,(x+12)*S,(y+h/2+12)*S], fill=edge, outline="#FFFFFF", width=2*S)
        tb = d.textbbox((0,0), tag, font=font(12, True))
        d.text((x*S-(tb[2]-tb[0])/2, (y+h/2)*S-(tb[3]-tb[1])/2-2*S), tag, font=font(12,True), fill="#FFFFFF")
    cy = y + h/2 - (10 if sub else 0)
    for txt, f, col, dy in [(title,F_BOX,text,0)] + ([(sub,F_BOXS,MUTED,19)] if sub else []):
        tb = d.textbbox((0,0), txt, font=f)
        d.text(((x+w/2)*S-(tb[2]-tb[0])/2, (cy+dy)*S-(tb[3]-tb[1])/2-2*S), txt, font=f, fill=col)
    B[k] = (x, y, w, h)

def A(k, side, off=0):
    x, y, w, h = B[k]
    return {"l":(x, y+h/2+off), "r":(x+w, y+h/2+off),
            "t":(x+w/2+off, y), "b":(x+w/2+off, y+h)}[side]

def head(p, dr, color):
    px, py, a = p[0]*S, p[1]*S, 6.5*S
    d.polygon({"r":[(px,py),(px-a,py-a*.6),(px-a,py+a*.6)],
               "l":[(px,py),(px+a,py-a*.6),(px+a,py+a*.6)],
               "d":[(px,py),(px-a*.6,py-a),(px+a*.6,py-a)],
               "u":[(px,py),(px-a*.6,py+a),(px+a*.6,py+a)]}[dr], fill=color)

def edge(pts, label=None, color=LINE, dashed=False, lbl_idx=0, lbl_dy=-10, lbl_t=0.5):
    for i in range(len(pts)-1):
        p, q = pts[i], pts[i+1]
        if dashed:
            dist = math.hypot(q[0]-p[0], q[1]-p[1]); n = max(int(dist/10), 1)
            for k in range(0, n, 2):
                t0, t1 = k/n, min((k+1)/n, 1)
                d.line([(p[0]+(q[0]-p[0])*t0)*S,(p[1]+(q[1]-p[1])*t0)*S,
                        (p[0]+(q[0]-p[0])*t1)*S,(p[1]+(q[1]-p[1])*t1)*S], fill=color, width=2*S)
        else:
            d.line([p[0]*S,p[1]*S,q[0]*S,q[1]*S], fill=color, width=2*S)
    p, q = pts[-2], pts[-1]
    head(q, "r" if q[0]>p[0]+1 else "l" if q[0]<p[0]-1 else "d" if q[1]>p[1] else "u", color)
    if label:
        p, q = pts[lbl_idx], pts[lbl_idx+1]
        lx, ly = p[0]+(q[0]-p[0])*lbl_t, p[1]+(q[1]-p[1])*lbl_t
        tb = d.textbbox((0,0), label, font=F_EDGE); tw, th = (tb[2]-tb[0])/S, (tb[3]-tb[1])/S
        d.rectangle([(lx-tw/2-4)*S,(ly+lbl_dy-th/2-3)*S,(lx+tw/2+4)*S,(ly+lbl_dy+th/2+4)*S], fill="#FFFFFF")
        d.text(((lx-tw/2)*S,(ly+lbl_dy-th/2)*S-2*S), label, font=F_EDGE, fill=MUTED)

d.text((44*S, 30*S), "POS Report Pipeline", font=F_TITLE, fill=INK)
d.text((45*S, 66*S), "Receive an aircraft position report, parse it, calculate remaining flight time and fuel, serve the latest status.",
       font=F_SUB, fill=MUTED)
d.line([44*S, 100*S, (W-44)*S, 100*S], fill="#E1E6EB", width=2*S)

BW, BH, SH = 176, 64, 58

# --- 1. ingest ---------------------------------------------------------
box("cl1", 44, 140, 132, BH, "client", "Client")
box("api1", 246, 140, BW, BH, "api", "API Gateway", "POST /pos-reports")
box("ing", 484, 140, BW, BH, "lambda", "IngestApi", "validate, derive flightId", tag="1")
box("raw", 722, 140, BW, BH, "s3", "S3   pos/", "raw message, verbatim")
edge([A("cl1","r"), A("api1","l")])
edge([A("api1","r"), A("ing","l")])
edge([A("ing","r"), A("raw","l")], "put")
edge([A("ing","b"), (573, 228), (110, 228), (110, 204)], "202  { flightId, status: RECEIVED }",
     color="#AEB8C2", dashed=True, lbl_idx=1, lbl_dy=-9)

# --- 2. parse ----------------------------------------------------------
box("par", 484, 330, BW, BH, "lambda", "Parser", "degrees/minutes -> decimal", tag="2")
edge([A("raw","b"), (810, 288), (572, 288), (572, 330)], "ObjectCreated,  prefix = pos/",
     lbl_idx=1, lbl_dy=-9, lbl_t=0.78)

box("att", 722, 270, BW, SH, "s3", "S3   attachment/", "parsed report JSON")
box("rep", 722, 348, BW, SH, "ddb", "DynamoDB", "pos-reports table")
box("sqs", 722, 426, BW, SH, "sqs", "SQS", "flightId + timestamp")
for dst, y in (("att", 299), ("rep", 377), ("sqs", 455)):
    edge([A("par","r"), (694, 362), (694, y), (722, y)])

box("dlq", 1000, 426, 132, SH, "dlq", "DLQ", "after 3 receives")
edge([A("sqs","r"), A("dlq","l")], color="#C7362F", dashed=True)

# --- 3. calculate ------------------------------------------------------
box("calc", 722, 546, BW, BH, "lambda", "Calculator", "haversine + fuel burn", tag="3")
edge([A("sqs","b"), A("calc","t")], "trigger", lbl_dy=-8)
edge([A("rep","r"), (938, 377), (938, 578), A("calc","r")], "read report",
     color=READ, dashed=True, lbl_idx=1, lbl_dy=0, lbl_t=0.80)

box("res", 484, 516, BW, SH, "s3", "S3   results/", "calculation JSON")
box("rres", 484, 594, BW, SH, "ddb", "DynamoDB", "results table")
for dst, y in (("res", 545), ("rres", 623)):
    edge([A("calc","l"), (694, 578), (694, y), (660, y)])

# --- 4. status ---------------------------------------------------------
box("cl2", 44, 760, 132, BH, "client", "Client")
box("api2", 246, 760, BW, BH, "api", "API Gateway", "GET /status/{flightId}")
box("st", 484, 760, BW, BH, "lambda", "StatusApi", "latest known state", tag="4")
edge([A("cl2","r"), A("api2","l")])
edge([A("api2","r"), A("st","l")])
edge([A("st","t"), (572, 730), (440, 730), (440, 623), (484, 623)], "query newest",
     color=READ, dashed=True, lbl_idx=1, lbl_dy=-9)
edge([(A("st","t")[0]+40, 760), (612, 730), (452, 730), (452, 545), (484, 545)],
     "read result JSON", color=READ, dashed=True, lbl_idx=2, lbl_dy=-9, lbl_t=0.20)

# ---- legend -----------------------------------------------------------
LX, LY = 968, 140
d.rounded_rectangle([LX*S, LY*S, (LX+308)*S, (LY+172)*S], radius=8*S,
                    fill="#FCFDFE", outline="#E1E6EB", width=2*S)
d.text(((LX+18)*S, (LY+16)*S), "AWS services", font=font(13, True), fill=INK)
for i, (kind, name) in enumerate([
        ("api", "API Gateway  (REST, 2 routes)"),
        ("lambda", "Lambda  (C# / .NET 8)"),
        ("s3", "S3  (pos/, attachment/, results/)"),
        ("ddb", "DynamoDB  (2 tables)"),
        ("sqs", "SQS  + dead-letter queue")]):
    fill, edge_c, _ = C[kind]
    y = LY + 46 + i*25
    d.rounded_rectangle([(LX+18)*S, y*S, (LX+38)*S, (y+14)*S], radius=3*S,
                        fill=fill, outline=edge_c, width=2*S)
    d.text(((LX+48)*S, (y-1)*S), name, font=F_BOXS, fill=MUTED)

d.text((44*S, 848*S), "Least privilege:  every Lambda has its own role, scoped to the exact API actions and S3 prefixes it uses.",
       font=F_SUB, fill=MUTED)
img.resize((W, H), Image.LANCZOS).save("docs/architecture.png")
print("wrote docs/architecture.png")
