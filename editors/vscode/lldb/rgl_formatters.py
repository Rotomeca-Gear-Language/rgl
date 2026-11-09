"""Les valeurs RGL lisibles dans le débogueur (lldb) : une `string` s'affiche "bonjour", un `symbol` §ok, un tableau `[…]` avec ses éléments.

Sans ces formateurs, une string est une structure (`{ctrl_:0x0, ptr_:0x0, ...}`) : le survol d'une variable ne dit rien d'utile. Les structures lues ici sont
celles du runtime (`runtime/include/rgl/standard/string.hpp`, `symbol.hpp`, `array.hpp`) ; elles ne changent pas d'un programme à l'autre.
Chargé par `command script import` (initCommands de l'extension) ; n'agit que sur les types du runtime RGL.
"""

import lldb

# Le plus de caractères montrés pour une string, et d'éléments pour un tableau.
MAX_TEXT = 200
MAX_ITEMS = 100

# Kind { Small, Heap, View } de string.hpp : Small garde le texte dans l'objet, les autres le pointent.
SMALL = 0


def _read(process, address, size):
    """Les `size` octets en `address`, ou None si la mémoire est illisible."""
    if address == 0 or size == 0:
        return b""
    error = lldb.SBError()
    data = process.ReadMemory(address, size, error)
    return bytes(data) if error.Success() else None


def _quote(raw):
    """Un texte RGL entre guillemets : ce que l'on écrit dans le source."""
    text = raw.decode("utf-8", errors="replace")
    escaped = text.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t")
    return '"' + escaped + '"'


def string_summary(valobj, _internal_dict):
    valobj = valobj.GetNonSyntheticValue()
    kind = valobj.GetChildMemberWithName("kind_").GetValueAsUnsigned()
    size = valobj.GetChildMemberWithName("size_").GetValueAsUnsigned()
    if kind == SMALL:
        address = valobj.GetChildMemberWithName("small_").GetLoadAddress()
    else:
        address = valobj.GetChildMemberWithName("ptr_").GetValueAsUnsigned()

    shown = min(size, MAX_TEXT)
    raw = _read(valobj.GetProcess(), address, shown)
    if raw is None:
        return "<texte illisible>"
    return _quote(raw) + ("…" if size > shown else "")


def symbol_summary(valobj, _internal_dict):
    valobj = valobj.GetNonSyntheticValue()
    entry = valobj.GetChildMemberWithName("entry_").Dereference()
    size = entry.GetChildMemberWithName("size").GetValueAsUnsigned()
    name = entry.GetChildMemberWithName("name").GetValueAsUnsigned()
    raw = _read(valobj.GetProcess(), name, min(size, MAX_TEXT))
    return "§" + (raw.decode("utf-8", errors="replace") if raw is not None else "?")


def array_summary(valobj, _internal_dict):
    size = valobj.GetNonSyntheticValue().GetChildMemberWithName("size_").GetValueAsUnsigned()
    return "[" + str(size) + (" élément" if size == 1 else " éléments") + "]"


class ArraySynth:
    """Les éléments d'un `array<T>` : ses `size_` premières cases, à partir de `data_`."""

    def __init__(self, valobj, _internal_dict):
        self.valobj = valobj
        self.update()

    def update(self):
        self.size = self.valobj.GetChildMemberWithName("size_").GetValueAsUnsigned()
        self.data = self.valobj.GetChildMemberWithName("data_")
        return False

    def has_children(self):
        return self.size > 0

    def num_children(self):
        return min(self.size, MAX_ITEMS)

    def get_child_index(self, name):
        try:
            return int(name.strip("[]"))
        except ValueError:
            return -1

    def get_child_at_index(self, index):
        if index < 0 or index >= self.num_children() or not self.data.IsValid():
            return None
        child = self.data.GetChildAtIndex(index, lldb.eNoDynamicValues, True)
        return child.Clone("[" + str(index) + "]") if child.IsValid() else None


def duration_summary(valobj, _internal_dict):
    """`1500ms`, `2s`, `3min`, `1h` : l'unité entière la plus grande (comme on l'écrit dans le source)."""
    milliseconds = valobj.GetNonSyntheticValue().GetChildMemberWithName("milliseconds_").GetValueAsSigned()
    for unit, size in (("h", 3600000), ("min", 60000), ("s", 1000)):
        if milliseconds != 0 and milliseconds % size == 0:
            return "%d%s" % (milliseconds // size, unit)
    return "%dms" % milliseconds


def datetime_summary(valobj, _internal_dict):
    """Un instant en UTC, comme `{d}` l'écrit : `2026-10-24T10:30:00Z` (`.mmm` si les millisecondes ne sont pas nulles)."""
    import datetime

    milliseconds = valobj.GetNonSyntheticValue().GetChildMemberWithName("milliseconds_").GetValueAsSigned()
    try:
        instant = datetime.datetime(1970, 1, 1) + datetime.timedelta(milliseconds=milliseconds)
    except (OverflowError, ValueError):
        return "%dms depuis 1970" % milliseconds
    text = instant.strftime("%Y-%m-%dT%H:%M:%S")
    return text + (".%03d" % (milliseconds % 1000) if milliseconds % 1000 else "") + "Z"


def optional_summary(valobj, _internal_dict):
    """`null` ou la valeur : `T?` s'écrit ainsi en RGL (l'enfant `Value` est celui que lldb donne à un `std::optional` qui a une valeur)."""
    # Un résumé reçoit la valeur sans sa vue synthétique : l'enfant « Value » de lldb n'existe que sur elle.
    synthetic = valobj.GetSyntheticValue()
    # Un seul enfant quand il a une valeur, aucun sinon (l'accès par nom échoue dans un résumé : par indice).
    value = synthetic.GetChildAtIndex(0) if synthetic.GetNumChildren() > 0 else None
    if value is None or not value.IsValid():
        return "null"
    return value.GetSummary() or value.GetValue() or "{…}"


class OpaqueSynth:
    """Un type primitif de RGL n'a pas de contenu à déplier : son résumé est sa valeur (les champs du runtime sont un détail du C++)."""

    def __init__(self, valobj, _internal_dict):
        self.valobj = valobj

    def update(self):
        return False

    def has_children(self):
        return False

    def num_children(self):
        return 0

    def get_child_at_index(self, _index):
        return None

    def get_child_index(self, _name):
        return -1


def __lldb_init_module(debugger, _internal_dict):
    module = __name__
    for command in (
        'type summary add -F %s.duration_summary -x "^rgl::standard::primitives::duration$" -w rgl' % module,
        'type summary add -F %s.datetime_summary -x "^rgl::standard::primitives::datetime$" -w rgl' % module,
        'type summary add -F %s.optional_summary -x "^std::(__1::|__cxx11::)?optional<.+>$" -w rgl' % module,
        'type synthetic add -l %s.OpaqueSynth -x "^rgl::standard::primitives::(string|symbol|duration|datetime)$" -w rgl' % module,
        'type summary add -F %s.string_summary -x "^rgl::standard::primitives::string$" -w rgl' % module,
        'type summary add -F %s.symbol_summary -x "^rgl::standard::primitives::symbol$" -w rgl' % module,
        'type summary add -F %s.array_summary -x "^rgl::standard::array<.+>$" -w rgl' % module,
        'type synthetic add -l %s.ArraySynth -x "^rgl::standard::array<.+>$" -w rgl' % module,
        "type category enable rgl",
    ):
        debugger.HandleCommand(command)
