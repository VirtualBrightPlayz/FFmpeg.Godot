extends Node3D

@export var ff_path: NodePath
@onready var ff: Node = get_node(ff_path)
@export var url: String

func _ready() -> void:
	ff.Play(url, url)
