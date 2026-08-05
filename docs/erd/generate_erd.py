"""
Generates the Data Intelligence Platform ERD as SVG and PDF.

Both outputs are rendered from the single model defined in TABLES/EDGES below, so they
cannot drift apart. Re-run this after any schema change:

    python docs/erd/generate_erd.py

Layout is hand-placed rather than auto-routed. An ERD is read far more often than it is
generated, and a deterministic layout keeps the diff meaningful when the schema moves.

Requires: reportlab (PDF only). No native dependencies.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field

# ---------------------------------------------------------------- geometry constants

HEADER_H = 40.0
ROW_H = 21.0
PAD_BOTTOM = 12.0
COL_W = 340.0
COL_X = [60.0, 460.0, 860.0]

CANVAS_W = 1300.0
CANVAS_H = 1500.0

# The legend sits under the subtitle rather than beside the title: at this width the
# title would run into it.
LEGEND_Y = 116.0

# ------------------------------------------------------------------------- palette

PALETTE = {
    "collect": {"accent": "#4F46E5", "tint": "#EEF2FF"},
    "core":    {"accent": "#0D9488", "tint": "#F0FDFA"},
    "sec":     {"accent": "#B45309", "tint": "#FFFBEB"},
    "ai":      {"accent": "#7C3AED", "tint": "#F5F3FF"},
}

INK = "#0F172A"
MUTED = "#64748B"
BORDER = "#CBD5E1"
EDGE = "#94A3B8"
EDGE_LABEL = "#475569"
PAGE_BG = "#FFFFFF"


@dataclass
class Column:
    name: str
    type: str
    tags: tuple[str, ...] = ()


@dataclass
class Table:
    key: str
    schema: str
    name: str
    x: float
    y: float
    columns: list[Column]
    note: str = ""
    w: float = COL_W

    @property
    def h(self) -> float:
        return HEADER_H + len(self.columns) * ROW_H + PAD_BOTTOM

    # Anchors, as a fraction along each edge, so routes stay stable if a table grows.
    def left(self, f: float = 0.5) -> tuple[float, float]:
        return (self.x, self.y + HEADER_H + (self.h - HEADER_H) * f)

    def right(self, f: float = 0.5) -> tuple[float, float]:
        return (self.x + self.w, self.y + HEADER_H + (self.h - HEADER_H) * f)

    def top(self, f: float = 0.5) -> tuple[float, float]:
        return (self.x + self.w * f, self.y)

    def bottom(self, f: float = 0.5) -> tuple[float, float]:
        return (self.x + self.w * f, self.y + self.h)


@dataclass
class Edge:
    """A foreign key. `points` are explicit waypoints; `one_at_start` marks the parent side."""
    points: list[tuple[float, float]]
    label: str
    one_at_start: bool = True
    cascade: bool = False
    label_offset: tuple[float, float] = (0.0, -7.0)

    def label_anchor(self) -> tuple[float, float]:
        """
        The point half way along the polyline by arc length.

        Not `points[len//2]`: for a two-point edge that is the *endpoint*, which puts the
        label underneath the table card it connects to, where it is silently clipped.
        """
        segments = list(zip(self.points, self.points[1:]))
        total = sum(((b[0] - a[0]) ** 2 + (b[1] - a[1]) ** 2) ** 0.5 for a, b in segments)
        if total == 0:
            return self.points[0]

        walked = 0.0
        for a, b in segments:
            seg = ((b[0] - a[0]) ** 2 + (b[1] - a[1]) ** 2) ** 0.5
            if walked + seg >= total / 2:
                f = (total / 2 - walked) / seg if seg else 0.0
                return (a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f)
            walked += seg
        return self.points[-1]


# --------------------------------------------------------------------------- model

Y_COLLECT = 190.0
Y_CORE = 530.0
Y_SEC = 940.0
Y_AI = 1190.0

TABLES: list[Table] = [
    # ---- collect: ingestion -------------------------------------------------
    Table("DataSource", "collect", "DataSource", COL_X[0], Y_COLLECT, [
        Column("DataSourceId", "TINYINT", ("PK",)),
        Column("Code", "VARCHAR(20)", ("UQ",)),
        Column("Name", "NVARCHAR(100)"),
        Column("Publisher", "NVARCHAR(100)"),
        Column("ApiEndpoint", "NVARCHAR(1000)"),
        Column("AccessMethod", "VARCHAR(20)"),
        Column("HttpMethod", "VARCHAR(6)"),
        Column("PublicationCadence", "VARCHAR(20)"),
        Column("IsEnabled", "BIT"),
    ], note="2 rows: BLS_CPI, NYFED_SOFR"),

    Table("CollectionRun", "collect", "CollectionRun", COL_X[1], Y_COLLECT, [
        Column("CollectionRunId", "BIGINT", ("PK",)),
        Column("DataSourceId", "TINYINT", ("FK", "UQ")),
        Column("ScheduledForUtc", "DATETIME2(0)", ("UQ",)),
        Column("Attempt", "TINYINT", ("UQ",)),
        Column("Status", "VARCHAR(20)"),
        Column("StartedAtUtc", "DATETIME2(3)"),
        Column("CompletedAtUtc", "DATETIME2(3)"),
        Column("ObservationsInserted", "INT"),
        Column("ObservationsRevised", "INT"),
        Column("FailureCategory", "VARCHAR(30)"),
    ], note="one row per attempt (FR-2)"),

    Table("RawPayload", "collect", "RawPayload", COL_X[2], Y_COLLECT, [
        Column("RawPayloadId", "BIGINT", ("PK",)),
        Column("CollectionRunId", "BIGINT", ("FK",)),
        Column("FetchedAtUtc", "DATETIME2(3)"),
        Column("ContentHash", "BINARY(32)"),
        Column("SizeBytes", "INT"),
        Column("CompressedContent", "VARBINARY(MAX)"),
    ], note="diagnostic; purge beyond ~90 days"),

    # ---- core: curated, one table per dataset -------------------------------
    Table("CpiObservation", "core", "CpiObservation", COL_X[0], Y_CORE, [
        Column("CpiObservationId", "BIGINT", ("PK",)),
        Column("SeriesCode", "VARCHAR(20)"),
        Column("ReferenceDate", "DATE"),
        Column("ReferenceYear", "SMALLINT", ("UQ", "UQf")),
        Column("PeriodCode", "VARCHAR(3)", ("UQ", "UQf")),
        Column("PeriodType", "VARCHAR(10)"),
        Column("IndexValue", "DECIMAL(12,3)"),
        Column("Footnotes", "VARCHAR(100)"),
        Column("RevisionNumber", "SMALLINT", ("UQ",)),
        Column("IsCurrent", "BIT"),
        Column("SupersededAtUtc", "DATETIME2(3)"),
        Column("CollectionRunId", "BIGINT", ("FK",)),
        Column("CollectedAtUtc", "DATETIME2(3)"),
        Column("RowHash", "BINARY(32)"),
    ], note="BLS CUUR0000SA0 only; one row per (year, period)"),

    Table("SofrDailyRate", "core", "SofrDailyRate", COL_X[1], Y_CORE, [
        Column("SofrDailyRateId", "BIGINT", ("PK",)),
        Column("RateType", "VARCHAR(5)"),
        Column("EffectiveDate", "DATE", ("UQ", "UQf")),
        Column("RatePercent", "DECIMAL(9,5)"),
        Column("Percentile1/25/75/99Percent", "DECIMAL(9,5)"),
        Column("VolumeUsdBillions", "DECIMAL(12,3)"),
        Column("Average30/90/180DayPercent", "DECIMAL(9,5)"),
        Column("SofrIndexValue", "DECIMAL(20,8)"),
        Column("RevisionIndicator", "CHAR(1)"),
        Column("FootnoteId", "VARCHAR(20)"),
        Column("RevisionNumber", "SMALLINT", ("UQ",)),
        Column("IsCurrent", "BIT"),
        Column("SupersededAtUtc", "DATETIME2(3)"),
        Column("CollectionRunId", "BIGINT", ("FK",)),
        Column("CollectedAtUtc", "DATETIME2(3)"),
        Column("RowHash", "BINARY(32)"),
    ], note="rate type SOFR only; one row per business day"),

    Table("RejectedObservation", "core", "RejectedObservation", COL_X[2], Y_CORE, [
        Column("RejectedObservationId", "BIGINT", ("PK",)),
        Column("CollectionRunId", "BIGINT", ("FK",)),
        Column("SeriesCode", "NVARCHAR(100)"),
        Column("ReferenceDateText", "NVARCHAR(50)"),
        Column("Reason", "VARCHAR(30)"),
        Column("ReasonDetail", "NVARCHAR(1000)"),
        Column("RejectedAtUtc", "DATETIME2(3)"),
    ], note="quarantine; EFFR / OBFR / TGCR / BGCR land here"),

    # ---- sec: identity (FR-9) ----------------------------------------------
    Table("AppUser", "sec", "AppUser", COL_X[0], Y_SEC, [
        Column("UserId", "INT", ("PK",)),
        Column("Email", "NVARCHAR(256)", ("UQ",)),
        Column("DisplayName", "NVARCHAR(150)"),
        Column("PasswordHash", "NVARCHAR(500)"),
        Column("IsActive", "BIT"),
    ]),

    Table("UserRole", "sec", "UserRole", COL_X[1], Y_SEC, [
        Column("UserId", "INT", ("PK", "FK")),
        Column("RoleId", "TINYINT", ("PK", "FK")),
        Column("GrantedAtUtc", "DATETIME2(3)"),
    ]),

    Table("Role", "sec", "Role", COL_X[2], Y_SEC, [
        Column("RoleId", "TINYINT", ("PK",)),
        Column("Name", "NVARCHAR(50)", ("UQ",)),
        Column("Description", "NVARCHAR(250)"),
    ], note="Administrator / Analyst / Viewer"),

    # ---- ai: assistant audit ------------------------------------------------
    Table("AssistantSession", "ai", "AssistantSession", COL_X[0], Y_AI, [
        Column("SessionId", "UNIQUEIDENTIFIER", ("PK",)),
        Column("UserId", "INT", ("FK",)),
        Column("StartedAtUtc", "DATETIME2(3)"),
    ]),

    Table("AssistantQuery", "ai", "AssistantQuery", COL_X[1], Y_AI, [
        Column("AssistantQueryId", "BIGINT", ("PK",)),
        Column("SessionId", "UNIQUEIDENTIFIER", ("FK",)),
        Column("UserId", "INT", ("FK",)),
        Column("QuestionText", "NVARCHAR(2000)"),
        Column("GeneratedSql", "NVARCHAR(MAX)"),
        Column("ValidationOutcome", "VARCHAR(20)"),
        Column("WasExecuted", "BIT"),
        Column("AnswerText", "NVARCHAR(MAX)"),
        Column("AskedAtUtc", "DATETIME2(3)"),
    ], note="every generated statement logged (auditability)"),

    Table("AssistantFeedback", "ai", "AssistantFeedback", COL_X[2], Y_AI, [
        Column("AssistantQueryId", "BIGINT", ("PK", "FK")),
        Column("IsHelpful", "BIT"),
        Column("Comment", "NVARCHAR(1000)"),
    ]),
]

BY_KEY = {t.key: t for t in TABLES}


def build_edges() -> list[Edge]:
    t = BY_KEY
    ds, run, raw = t["DataSource"], t["CollectionRun"], t["RawPayload"]
    cpi, sofr, rej = t["CpiObservation"], t["SofrDailyRate"], t["RejectedObservation"]
    usr, ur, rol = t["AppUser"], t["UserRole"], t["Role"]
    ses, qry, fb = t["AssistantSession"], t["AssistantQuery"], t["AssistantFeedback"]

    # A horizontal bus between the collect and core bands keeps the three edges that
    # descend from CollectionRun from overlapping each other.
    bus = Y_CORE - 46

    edges = [
        Edge([ds.right(0.35), run.left(0.35)], "1 : N"),
        Edge([run.right(0.30), raw.left(0.30)], "1 : N", cascade=True),

        # CollectionRun is the only parent the core tables have: every stored row says
        # which attempt produced it. Fanned out along the bus so the three routes stay
        # individually traceable.
        Edge([run.bottom(0.22), (run.x + run.w * 0.22, bus - 30),
              (cpi.x + cpi.w * 0.55, bus - 30), cpi.top(0.55)], "1 : N",
             label_offset=(-6, -6)),
        Edge([run.bottom(0.55), sofr.top(0.55)], "1 : N", label_offset=(20, -6)),
        Edge([run.bottom(0.86), (run.x + run.w * 0.86, bus - 30),
              (rej.x + rej.w * 0.45, bus - 30), rej.top(0.45)], "1 : N",
             cascade=True, label_offset=(6, -6)),

        # sec
        Edge([usr.right(0.30), ur.left(0.30)], "1 : N", cascade=True),
        Edge([rol.left(0.40), ur.right(0.40)], "1 : N"),

        # ai
        Edge([qry.left(0.35), ses.right(0.35)], "N : 1", one_at_start=False),
        Edge([fb.left(0.40), qry.right(0.40)], "1 : 1", one_at_start=False),

        # AppUser -> AssistantSession, and AppUser -> AssistantQuery.
        Edge([usr.bottom(0.20), ses.top(0.20)], "1 : N", label_offset=(8, -6)),
        Edge([usr.bottom(0.62), (usr.x + usr.w * 0.62, Y_AI - 30),
              (qry.x + qry.w * 0.22, Y_AI - 30), qry.top(0.22)], "1 : N",
             label_offset=(8, -6)),
    ]
    return edges


EDGES = build_edges()

TITLE = "Data Intelligence Platform - Entity Relationship Diagram"
SUBTITLE = ("Phase 3 deliverable (SOW 6).  CPI series CUUR0000SA0 (BLS) and the Secured "
            "Overnight Financing Rate (NY Fed), one table each.")
FOOTER = ("Generated from docs/erd/generate_erd.py - regenerate after any schema change.  "
          "Mirrors docs/database-schema.sql.  SofrDailyRate's four percentile and three "
          "average columns are shown collapsed.  analytics.* is views over core.*, not shown.")

LEGEND = [
    ("PK", "Primary key"),
    ("FK", "Foreign key"),
    ("UQ", "Part of a unique key"),
    ("UQf", "Unique where IsCurrent = 1"),
]


# ----------------------------------------------------------------------- SVG output

def esc(s: str) -> str:
    return (s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;"))


def svg_tag_pill(x: float, y: float, text: str, accent: str) -> str:
    w = 10 + len(text) * 5.6
    return (
        f'<rect x="{x:.1f}" y="{y - 8.5:.1f}" width="{w:.1f}" height="12" rx="3" '
        f'fill="{accent}" fill-opacity="0.13"/>'
        f'<text x="{x + w / 2:.1f}" y="{y + 0.5:.1f}" text-anchor="middle" '
        f'font-size="8.5" font-weight="600" fill="{accent}">{esc(text)}</text>'
    )


def render_svg() -> str:
    parts: list[str] = []
    parts.append(
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{CANVAS_W:.0f}" '
        f'height="{CANVAS_H:.0f}" viewBox="0 0 {CANVAS_W:.0f} {CANVAS_H:.0f}" '
        f'font-family="Segoe UI, Inter, Helvetica, Arial, sans-serif">'
    )
    parts.append(
        '<defs><filter id="card" x="-4%" y="-4%" width="108%" height="112%">'
        '<feDropShadow dx="0" dy="1.5" stdDeviation="2.5" flood-color="#0F172A" '
        'flood-opacity="0.10"/></filter></defs>'
    )
    parts.append(f'<rect width="100%" height="100%" fill="{PAGE_BG}"/>')

    # Title block
    parts.append(f'<text x="60" y="62" font-size="26" font-weight="700" fill="{INK}">'
                 f'{esc(TITLE)}</text>')
    parts.append(f'<text x="60" y="88" font-size="13.5" fill="{MUTED}">{esc(SUBTITLE)}</text>')

    # Legend, right-aligned under the title block.
    lx = CANVAS_W - 60
    seg = 0.0
    for tag, meaning in reversed(LEGEND):
        label_w = len(meaning) * 6.4
        seg += label_w + 46
    x = lx - seg
    for tag, meaning in LEGEND:
        parts.append(svg_tag_pill(x, LEGEND_Y, tag, INK))
        tw = 10 + len(tag) * 5.6
        parts.append(f'<text x="{x + tw + 7:.1f}" y="{LEGEND_Y + 4:.1f}" font-size="11.5" '
                     f'fill="{MUTED}">{esc(meaning)}</text>')
        x += tw + 7 + len(meaning) * 6.4 + 24

    # Edges first so cards overlay their endpoints.
    for e in EDGES:
        pts = " ".join(f"{x:.1f},{y:.1f}" for x, y in e.points)
        dash = ' stroke-dasharray="5 4"' if e.cascade else ""
        parts.append(f'<polyline points="{pts}" fill="none" stroke="{EDGE}" '
                     f'stroke-width="1.6"{dash} stroke-linejoin="round"/>')

        one = e.points[0] if e.one_at_start else e.points[-1]
        many = e.points[-1] if e.one_at_start else e.points[0]
        nxt_one = e.points[1] if e.one_at_start else e.points[-2]

        # "One" end: a short perpendicular tick. "Many" end: a filled disc.
        dx, dy = nxt_one[0] - one[0], nxt_one[1] - one[1]
        ln = max((dx * dx + dy * dy) ** 0.5, 0.001)
        px, py = -dy / ln * 5.5, dx / ln * 5.5
        ox, oy = one[0] + dx / ln * 7, one[1] + dy / ln * 7
        parts.append(f'<line x1="{ox - px:.1f}" y1="{oy - py:.1f}" x2="{ox + px:.1f}" '
                     f'y2="{oy + py:.1f}" stroke="{EDGE}" stroke-width="2"/>')
        parts.append(f'<circle cx="{many[0]:.1f}" cy="{many[1]:.1f}" r="3.6" fill="{EDGE}"/>')

        mid = e.label_anchor()
        parts.append(
            f'<text x="{mid[0] + e.label_offset[0]:.1f}" y="{mid[1] + e.label_offset[1]:.1f}" '
            f'font-size="10.5" fill="{EDGE_LABEL}" text-anchor="middle">{esc(e.label)}</text>')

    # Schema band labels
    for schema, y in (("collect", Y_COLLECT), ("core", Y_CORE), ("sec", Y_SEC), ("ai", Y_AI)):
        accent = PALETTE[schema]["accent"]
        parts.append(f'<text x="60" y="{y - 16:.1f}" font-size="12.5" font-weight="700" '
                     f'fill="{accent}" letter-spacing="1.2">{esc(schema.upper())}</text>')

    # Tables
    for t in TABLES:
        accent = PALETTE[t.schema]["accent"]
        tint = PALETTE[t.schema]["tint"]

        parts.append(f'<g filter="url(#card)"><rect x="{t.x:.1f}" y="{t.y:.1f}" '
                     f'width="{t.w:.1f}" height="{t.h:.1f}" rx="10" fill="#FFFFFF" '
                     f'stroke="{BORDER}" stroke-width="1"/></g>')
        parts.append(f'<path d="M{t.x:.1f},{t.y + 10:.1f} a10,10 0 0 1 10,-10 '
                     f'h{t.w - 20:.1f} a10,10 0 0 1 10,10 v{HEADER_H - 10:.1f} '
                     f'h{-t.w:.1f} z" fill="{accent}"/>')
        parts.append(f'<text x="{t.x + 14:.1f}" y="{t.y + 25:.1f}" font-size="14.5" '
                     f'font-weight="700" fill="#FFFFFF">{esc(t.name)}</text>')
        parts.append(f'<text x="{t.x + t.w - 14:.1f}" y="{t.y + 25:.1f}" font-size="10.5" '
                     f'fill="#FFFFFF" fill-opacity="0.8" text-anchor="end">'
                     f'{esc(t.schema)}</text>')

        y = t.y + HEADER_H
        for i, c in enumerate(t.columns):
            if i % 2 == 1:
                parts.append(f'<rect x="{t.x + 1:.1f}" y="{y:.1f}" width="{t.w - 2:.1f}" '
                             f'height="{ROW_H:.1f}" fill="{tint}"/>')
            ty = y + 14.5
            bold = ' font-weight="650"' if "PK" in c.tags else ""
            parts.append(f'<text x="{t.x + 14:.1f}" y="{ty:.1f}" font-size="11.5" '
                         f'fill="{INK}"{bold}>{esc(c.name)}</text>')

            tx = t.x + 14 + len(c.name) * 6.35 + 7
            for tag in c.tags:
                parts.append(svg_tag_pill(tx, ty, tag, accent))
                tx += 10 + len(tag) * 5.6 + 4

            parts.append(f'<text x="{t.x + t.w - 14:.1f}" y="{ty:.1f}" font-size="10" '
                         f'fill="{MUTED}" text-anchor="end">{esc(c.type)}</text>')
            y += ROW_H

        if t.note:
            parts.append(f'<text x="{t.x + 14:.1f}" y="{t.y + t.h - 3:.1f}" font-size="9.5" '
                         f'font-style="italic" fill="{MUTED}">{esc(t.note)}</text>')

    parts.append(f'<text x="60" y="{CANVAS_H - 34:.1f}" font-size="11" fill="{MUTED}">'
                 f'{esc(FOOTER)}</text>')
    parts.append('</svg>')
    return "\n".join(parts)


# ----------------------------------------------------------------------- PDF output

def render_pdf(path: str) -> None:
    from reportlab.lib.colors import HexColor
    from reportlab.pdfgen import canvas as pdfcanvas

    c = pdfcanvas.Canvas(path, pagesize=(CANVAS_W, CANVAS_H))
    c.setTitle("Data Intelligence Platform - ERD")
    c.setSubject("Phase 3 deliverable: entity relationship diagram")

    def Y(v: float) -> float:
        """SVG is top-down, PDF is bottom-up."""
        return CANVAS_H - v

    c.setFillColor(HexColor(PAGE_BG))
    c.rect(0, 0, CANVAS_W, CANVAS_H, stroke=0, fill=1)

    c.setFillColor(HexColor(INK))
    c.setFont("Helvetica-Bold", 26)
    c.drawString(60, Y(62), TITLE)
    c.setFont("Helvetica", 13.5)
    c.setFillColor(HexColor(MUTED))
    c.drawString(60, Y(88), SUBTITLE)

    # Legend
    seg = sum(len(m) * 6.4 + 46 for _, m in LEGEND)
    x = CANVAS_W - 60 - seg
    for tag, meaning in LEGEND:
        tw = 10 + len(tag) * 5.6
        c.setFillColor(HexColor(INK), alpha=0.13)
        c.roundRect(x, Y(LEGEND_Y) - 8.5, tw, 12, 3, stroke=0, fill=1)
        c.setFillColor(HexColor(INK), alpha=1)
        c.setFont("Helvetica-Bold", 8.5)
        c.drawCentredString(x + tw / 2, Y(LEGEND_Y) - 5.5, tag)
        c.setFont("Helvetica", 11.5)
        c.setFillColor(HexColor(MUTED))
        c.drawString(x + tw + 7, Y(LEGEND_Y + 4) + 0.5, meaning)
        x += tw + 7 + len(meaning) * 6.4 + 24

    # Edges
    for e in EDGES:
        c.setStrokeColor(HexColor(EDGE))
        c.setLineWidth(1.6)
        c.setDash(5, 4) if e.cascade else c.setDash()
        p = c.beginPath()
        p.moveTo(e.points[0][0], Y(e.points[0][1]))
        for px_, py_ in e.points[1:]:
            p.lineTo(px_, Y(py_))
        c.drawPath(p, stroke=1, fill=0)
        c.setDash()

        one = e.points[0] if e.one_at_start else e.points[-1]
        many = e.points[-1] if e.one_at_start else e.points[0]
        nxt = e.points[1] if e.one_at_start else e.points[-2]

        dx, dy = nxt[0] - one[0], nxt[1] - one[1]
        ln = max((dx * dx + dy * dy) ** 0.5, 0.001)
        px2, py2 = -dy / ln * 5.5, dx / ln * 5.5
        ox, oy = one[0] + dx / ln * 7, one[1] + dy / ln * 7
        c.setLineWidth(2)
        c.line(ox - px2, Y(oy - py2), ox + px2, Y(oy + py2))

        c.setFillColor(HexColor(EDGE))
        c.circle(many[0], Y(many[1]), 3.6, stroke=0, fill=1)

        mid = e.label_anchor()
        c.setFont("Helvetica", 10.5)
        c.setFillColor(HexColor(EDGE_LABEL))
        c.drawCentredString(mid[0] + e.label_offset[0], Y(mid[1] + e.label_offset[1]), e.label)

    # Band labels
    for schema, y in (("collect", Y_COLLECT), ("core", Y_CORE), ("sec", Y_SEC), ("ai", Y_AI)):
        c.setFillColor(HexColor(PALETTE[schema]["accent"]))
        c.setFont("Helvetica-Bold", 12.5)
        c.drawString(60, Y(y - 16), schema.upper())

    # Tables
    for t in TABLES:
        accent = HexColor(PALETTE[t.schema]["accent"])
        tint = HexColor(PALETTE[t.schema]["tint"])

        c.setFillColor(HexColor("#FFFFFF"))
        c.setStrokeColor(HexColor(BORDER))
        c.setLineWidth(1)
        c.roundRect(t.x, Y(t.y + t.h), t.w, t.h, 10, stroke=1, fill=1)

        # Header band: a rounded rect clipped to the top by a covering square.
        c.saveState()
        path = c.beginPath()
        path.roundRect(t.x, Y(t.y + t.h), t.w, t.h, 10)
        c.clipPath(path, stroke=0, fill=0)
        c.setFillColor(accent)
        c.rect(t.x, Y(t.y + HEADER_H), t.w, HEADER_H, stroke=0, fill=1)
        c.restoreState()

        c.setFillColor(HexColor("#FFFFFF"))
        c.setFont("Helvetica-Bold", 14.5)
        c.drawString(t.x + 14, Y(t.y + 25), t.name)
        c.setFont("Helvetica", 10.5)
        c.setFillColor(HexColor("#FFFFFF"), alpha=0.8)
        c.drawRightString(t.x + t.w - 14, Y(t.y + 25), t.schema)
        c.setFillColor(HexColor("#FFFFFF"), alpha=1)

        y = t.y + HEADER_H
        for i, col in enumerate(t.columns):
            if i % 2 == 1:
                c.setFillColor(tint)
                c.rect(t.x + 1, Y(y + ROW_H), t.w - 2, ROW_H, stroke=0, fill=1)

            ty = y + 14.5
            c.setFillColor(HexColor(INK))
            c.setFont("Helvetica-Bold" if "PK" in col.tags else "Helvetica", 11.5)
            c.drawString(t.x + 14, Y(ty), col.name)

            tx = t.x + 14 + len(col.name) * 6.35 + 7
            for tag in col.tags:
                tw = 10 + len(tag) * 5.6
                c.setFillColor(accent, alpha=0.13)
                c.roundRect(tx, Y(ty) - 3, tw, 12, 3, stroke=0, fill=1)
                c.setFillColor(accent, alpha=1)
                c.setFont("Helvetica-Bold", 8.5)
                c.drawCentredString(tx + tw / 2, Y(ty), tag)
                tx += tw + 4

            c.setFont("Helvetica", 10)
            c.setFillColor(HexColor(MUTED))
            c.drawRightString(t.x + t.w - 14, Y(ty), col.type)
            y += ROW_H

        if t.note:
            c.setFont("Helvetica-Oblique", 9.5)
            c.setFillColor(HexColor(MUTED))
            c.drawString(t.x + 14, Y(t.y + t.h - 3), t.note)

    c.setFont("Helvetica", 11)
    c.setFillColor(HexColor(MUTED))
    c.drawString(60, Y(CANVAS_H - 34), FOOTER)

    c.showPage()
    c.save()


# --------------------------------------------------------------------- self-checks

def validate() -> list[str]:
    """Catches the layout mistakes that are invisible in code but obvious on the page."""
    problems: list[str] = []

    for a in TABLES:
        if a.y + a.h > CANVAS_H - 60:
            problems.append(f"{a.key} overflows the canvas bottom")
        if a.x + a.w > CANVAS_W - 40:
            problems.append(f"{a.key} overflows the canvas right edge")

    for i, a in enumerate(TABLES):
        for b in TABLES[i + 1:]:
            if (a.x < b.x + b.w and b.x < a.x + a.w
                    and a.y < b.y + b.h and b.y < a.y + a.h):
                problems.append(f"{a.key} overlaps {b.key}")

    for e in EDGES:
        if len(e.points) < 2:
            problems.append(f"edge '{e.label}' has fewer than two points")

    return problems


def main() -> None:
    here = os.path.dirname(os.path.abspath(__file__))
    docs = os.path.dirname(here)

    problems = validate()
    if problems:
        for p in problems:
            print(f"LAYOUT PROBLEM: {p}")
        raise SystemExit(1)

    svg_path = os.path.join(docs, "database-erd.svg")
    pdf_path = os.path.join(docs, "database-erd.pdf")

    with open(svg_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(render_svg())
    render_pdf(pdf_path)

    print(f"{len(TABLES)} tables, {len(EDGES)} relationships")
    print(f"wrote {svg_path} ({os.path.getsize(svg_path):,} bytes)")
    print(f"wrote {pdf_path} ({os.path.getsize(pdf_path):,} bytes)")


if __name__ == "__main__":
    main()
