extends SceneTree


# 機能: 生成済み全GLBをGodot標準ローダーで読み、シーン生成を確認する。
# 引数: なし。
# 返り値: なし。成功時0、失敗時1でプロセスを終了する。
func _initialize() -> void:
	var manifest = JSON.parse_string(FileAccess.get_file_as_string("res://Data/RealWorld/plateau-manifest.json"))
	if not manifest is Dictionary:
		push_error("PLATEAUマニフェストを読めません。")
		quit(1)
		return
	var count: int = 0
	for tile in manifest["tiles"]:
		var file = FileAccess.open(tile["path"], FileAccess.READ)
		if file == null:
			push_error("GLBが存在しません: " + tile["path"])
			quit(1)
			return
		var document = GLTFDocument.new()
		var state = GLTFState.new()
		var result = document.append_from_buffer(file.get_buffer(file.get_length()), "res://Assets/RealWorld/Plateau/", state)
		file.close()
		if result != OK:
			push_error("GLBの読み込み失敗: " + tile["path"])
			quit(1)
			return
		var scene = document.generate_scene(state)
		if scene == null or state.get_meshes().size() == 0:
			push_error("GLBのシーン生成失敗: " + tile["path"])
			quit(1)
			return
		scene.free()
		count += 1
	print("PLATEAU全GLB読込確認成功: ", count, " タイル、実建築物 ", manifest["statistics"]["buildingCount"], " 棟")
	quit(0)
