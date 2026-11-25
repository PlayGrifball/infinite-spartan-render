# Image build step: put Ekur's name table where the add-on looks for it, so nothing is written at run time.
import shutil
import bpy
shutil.copy("/opt/ekur/assets/regions_and_permutations.json", bpy.utils.extension_path_user("bl_ext.user_default.ekur", create=True))
